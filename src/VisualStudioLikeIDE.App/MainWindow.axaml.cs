using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit.Highlighting;
using VisualStudioLikeIDE.NodeEditor;
using VisualStudioLikeIDE.Compiler;
using VisualStudioLikeIDE.Compiler.Diagnostics;
using VisualStudioLikeIDE.Compiler.Emit;
using VisualStudioLikeIDE.Compiler.Nodes;
using VisualStudioLikeIDE.GraphModel;
using VisualStudioLikeIDE.ProjectSystem;

namespace VisualStudioLikeIDE.App;

/// <summary>
/// The IDE shell: wires the palette, canvas, inspector, problems, generated code, and output panels to the open workspace.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The build cancellation source and the running process are disposed when each operation completes; windows are not disposed by their owner.")]
public sealed partial class MainWindow : Window
{
    private const string Configuration = "Debug";

    private readonly DispatcherTimer recompileTimer;
    private readonly NodeLibrary paletteLibrary = NodeLibrary.CreateDefault();
    private Workspace? workspace;
    private CancellationTokenSource? buildCancellation;
    private DotNetProcess? runningProgram;
    private bool busy;
    private bool closeConfirmed;
    private bool syncingCodeSelection;

    public MainWindow()
    {
        InitializeComponent();

        recompileTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        recompileTimer.Tick += (_, _) =>
        {
            recompileTimer.Stop();
            Recompile();
        };

        CodeEditor.SyntaxHighlighting = DarkCSharpHighlighting();
        CodeEditor.TextArea.Caret.PositionChanged += (_, _) => OnCodeCaretMoved();

        PaletteSearch.TextChanged += (_, _) => PopulatePalette();
        PaletteSearch.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && PaletteList.ItemCount > 0)
            {
                PaletteList.SelectedIndex = 0;
                (PaletteList.ContainerFromIndex(0) as ListBoxItem)?.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                AddNodeFromPalette(PaletteList.ItemCount > 0 ? PaletteList.Items[0] as ListBoxItem : null);
                e.Handled = true;
            }
        };
        PaletteList.DoubleTapped += (_, _) => AddNodeFromPalette(PaletteList.SelectedItem as ListBoxItem);
        PaletteList.AddHandler(PointerPressedEvent, OnPalettePointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        PaletteList.AddHandler(PointerMovedEvent, OnPalettePointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        PaletteList.AddHandler(PointerReleasedEvent, OnPalettePointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        PaletteList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                AddNodeFromPalette(PaletteList.SelectedItem as ListBoxItem);
                e.Handled = true;
            }
        };

        GraphList.SelectionChanged += (_, _) => OnGraphListSelectionChanged();

        ProblemsList.DoubleTapped += (_, _) => GoToDiagnostic(ProblemsList.SelectedItem as ListBoxItem);
        ProblemsList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                GoToDiagnostic(ProblemsList.SelectedItem as ListBoxItem);
            }
        };

        InputBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                SendInput();
                e.Handled = true;
            }
        };

        GraphCanvas.SelectionChanged += (_, _) => OnCanvasSelectionChanged();
        GraphCanvas.StatusChanged += (_, message) => SetStatus(message);
        GraphCanvas.WireDropped += (_, e) => ShowQuickAdd(e);
        // Tunnel: runs before the canvas handles the press, so a double-click that opens the inline editor comes after this.
        GraphCanvas.AddHandler(PointerPressedEvent, (_, _) =>
        {
            HideQuickAdd();
            HideCommandPalette(focusGraph: false);
            EndInlineEdit(commit: true);
        }, RoutingStrategies.Tunnel);
        GraphCanvas.ValueEditRequested += (_, e) => BeginValueEdit(e);
        GraphCanvas.CommentEditRequested += (_, e) => BeginCommentEdit(e);
        InlineEditor.TextChanged += (_, _) => ValidateInlineEdit();
        InlineEditor.LostFocus += (_, _) => EndInlineEdit(commit: true);
        // Tunnel: a multi-line TextBox consumes Enter itself before bubbling handlers see it.
        InlineEditor.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                EndInlineEdit(commit: true);
                GraphCanvas.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                EndInlineEdit(commit: false);
                GraphCanvas.Focus();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);

        QuickAddSearch.TextChanged += (_, _) => PopulateQuickAdd();
        QuickAddSearch.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && QuickAddList.ItemCount > 0)
            {
                QuickAddList.SelectedIndex = 0;
                (QuickAddList.ContainerFromIndex(0) as ListBoxItem)?.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                AddNodeFromQuickAdd(QuickAddList.ItemCount > 0 ? QuickAddList.Items[0] as ListBoxItem : null);
                e.Handled = true;
            }
        };
        QuickAddList.Tapped += (_, _) => AddNodeFromQuickAdd(QuickAddList.SelectedItem as ListBoxItem);
        QuickAddList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                AddNodeFromQuickAdd(QuickAddList.SelectedItem as ListBoxItem);
                e.Handled = true;
            }
        };
        QuickAdd.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                HideQuickAdd();
                GraphCanvas.Focus();
                e.Handled = true;
            }
        };

        CommandSearch.TextChanged += (_, _) => PopulateCommandPalette();
        CommandSearch.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && CommandList.ItemCount > 1)
            {
                CommandList.SelectedIndex = 1;
                (CommandList.ContainerFromIndex(1) as ListBoxItem)?.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                RunPaletteCommand(CommandList.SelectedItem as ListBoxItem);
                e.Handled = true;
            }
        };
        CommandList.Tapped += (_, _) => RunPaletteCommand(CommandList.SelectedItem as ListBoxItem);
        CommandList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                RunPaletteCommand(CommandList.SelectedItem as ListBoxItem);
                e.Handled = true;
            }
        };
        CommandPalette.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                HideCommandPalette(focusGraph: true);
                e.Handled = true;
            }
        };

        PopulatePalette();
        UpdateCommandStates();
        ShowInspector();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }

        bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        // Clipboard shortcuts act on nodes only while the graph has focus; text boxes and code views keep their own.
        bool graph = GraphCanvas.IsKeyboardFocusWithin;
        Action? command = (e.Key, control, shift) switch
        {
            (Key.N, true, false) => () => OnNewProject(this, e),
            (Key.O, true, false) => () => OnOpenProject(this, e),
            (Key.S, true, false) => () => OnSave(this, e),
            (Key.Z, true, false) => () => OnUndo(this, e),
            (Key.Y, true, false) or (Key.Z, true, true) => () => OnRedo(this, e),
            (Key.B, true, true) => () => OnBuild(this, e),
            (Key.P, true, true) => ShowCommandPalette,
            (Key.X, true, false) when graph => () => OnCut(this, e),
            (Key.C, true, false) when graph => () => OnCopy(this, e),
            (Key.V, true, false) when graph => () => OnPaste(this, e),
            (Key.D, true, false) when graph => () => OnDuplicate(this, e),
            (Key.L, true, false) when graph => ShowConnectPicker,
            (Key.F5, false, false) => () => OnRun(this, e),
            (Key.F5, false, true) => () => OnStop(this, e),
            (Key.F6, false, false) => () => OnGenerate(this, e),
            _ => null,
        };

        if (command is not null)
        {
            command();
            e.Handled = true;
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!closeConfirmed && workspace?.IsDirty == true)
        {
            e.Cancel = true;
            ClosePrompt.IsVisible = true;
            return;
        }

        runningProgram?.Stop();
        buildCancellation?.Cancel();
    }

    private async void OnNewProject(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose an empty folder for the new project",
            AllowMultiple = false,
        });
        if (folders is not [var folder] || folder.TryGetLocalPath() is not { } path)
        {
            return;
        }

        string name = ProjectNameFromFolder(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)));
        try
        {
            OpenWorkspace(VisualProject.Create(path, name));
            SetStatus($"Created project {name} in {path}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ReportError("Could not create the project", exception);
        }
    }

    private async void OnOpenProject(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a visual project",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Visual project") { Patterns = ["*" + VisualProject.FileExtension] }],
        });
        if (files is [var file] && file.TryGetLocalPath() is { } path)
        {
            OpenProjectFile(path);
        }
    }

    /// <summary>Opens a project file. Opening never builds or runs anything; that always requires an explicit command.</summary>
    internal void OpenProjectFile(string path)
    {
        try
        {
            OpenWorkspace(VisualProject.Load(path));
            SetStatus($"Opened {path}.");
        }
        catch (Exception exception) when (exception is GraphFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportError("Could not open the project", exception);
        }
    }

    private void OnSave(object? sender, RoutedEventArgs e) => TrySave();

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    private void OnUndo(object? sender, RoutedEventArgs e) => workspace?.Editor.Undo();

    private void OnRedo(object? sender, RoutedEventArgs e) => workspace?.Editor.Redo();

    private void OnDelete(object? sender, RoutedEventArgs e) => GraphCanvas.DeleteSelection();

    private async void OnCut(object? sender, RoutedEventArgs e) => await CopySelectionAsync(cut: true);

    private async void OnCopy(object? sender, RoutedEventArgs e) => await CopySelectionAsync(cut: false);

    private async void OnPaste(object? sender, RoutedEventArgs e) => await PasteAsync();

    private void OnDuplicate(object? sender, RoutedEventArgs e)
    {
        if (workspace is null || GraphCanvas.SelectedItems.Count == 0)
        {
            return;
        }

        GraphDocument fragment = workspace.Editor.CopyFragment([.. GraphCanvas.SelectedItems]);
        double left = fragment.Nodes.Select(node => node.X).Concat(fragment.Comments.Select(comment => comment.X)).Min();
        double top = fragment.Nodes.Select(node => node.Y).Concat(fragment.Comments.Select(comment => comment.Y)).Min();
        PasteFragment(fragment, new Avalonia.Point(left + 40, top + 40));
    }

    private void OnSelectAll(object? sender, RoutedEventArgs e) => GraphCanvas.SelectAll();

    private void OnAddComment(object? sender, RoutedEventArgs e) => GraphCanvas.AddComment();

    private void OnFrameAll(object? sender, RoutedEventArgs e) => GraphCanvas.FrameAll();

    private void OnAlign(object? sender, RoutedEventArgs e) =>
        GraphCanvas.AlignSelection(Enum.Parse<Alignment>((string)((MenuItem)sender!).Tag!));

    private void OnDistribute(object? sender, RoutedEventArgs e) =>
        GraphCanvas.DistributeSelection(Enum.Parse<Axis>((string)((MenuItem)sender!).Tag!));

    private void OnConnectPins(object? sender, RoutedEventArgs e) => ShowConnectPicker();

    private void OnShowCommandPalette(object? sender, RoutedEventArgs e) => ShowCommandPalette();

    private void OnToggleMinimap(object? sender, RoutedEventArgs e) => GraphCanvas.ShowMinimap = MinimapMenuItem.IsChecked;

    private void OnToggleFrameStats(object? sender, RoutedEventArgs e) => GraphCanvas.ShowFrameStats = FrameStatsMenuItem.IsChecked;

    private async void OnGenerate(object? sender, RoutedEventArgs e) => await GenerateAsync(showOutput: false);

    private async void OnBuild(object? sender, RoutedEventArgs e) => await BuildAsync();

    private async void OnRun(object? sender, RoutedEventArgs e)
    {
        if (!await BuildAsync())
        {
            return;
        }

        try
        {
            AppendOutput($"> dotnet run --project {workspace!.Project.CsprojPath}");
            runningProgram = DotNetCli.StartRun(workspace.Project.CsprojPath, Configuration, line => Dispatcher.UIThread.Post(() => AppendOutput(line)));
            UpdateCommandStates();
            SetStatus("Running. Type input below the output and press Enter.");
            InputBox.Focus();
            int exitCode = await runningProgram.WaitForExitAsync();
            AppendOutput($"Program exited with code {exitCode}.");
            SetStatus($"Program exited with code {exitCode}.");
        }
        catch (InvalidOperationException exception)
        {
            ReportError("Could not run the program", exception);
        }
        finally
        {
            runningProgram?.Dispose();
            runningProgram = null;
            UpdateCommandStates();
        }
    }

    private void OnStop(object? sender, RoutedEventArgs e)
    {
        buildCancellation?.Cancel();
        runningProgram?.Stop();
    }

    private void OnSendInput(object? sender, RoutedEventArgs e) => SendInput();

    private void OnSaveAndClose(object? sender, RoutedEventArgs e)
    {
        if (TrySave())
        {
            closeConfirmed = true;
            Close();
        }
    }

    private void OnDiscardAndClose(object? sender, RoutedEventArgs e)
    {
        closeConfirmed = true;
        Close();
    }

    private void OnCancelClose(object? sender, RoutedEventArgs e) => ClosePrompt.IsVisible = false;

    private void OpenWorkspace(VisualProject project)
    {
        if (workspace is not null)
        {
            workspace.Changed -= OnGraphChanged;
        }

        workspace = Workspace.Open(project);
        workspace.Changed += OnGraphChanged;
        EmptyState.IsVisible = false;
        OutputEditor.Text = "";
        ProjectText.Text = $"{project.Name}  ·  {project.DirectoryPath}";
        ShowOpenGraph();
    }

    /// <summary>Shows the workspace's open graph in the canvas, palette, graph list, and panels.</summary>
    private void ShowOpenGraph()
    {
        HideQuickAdd();
        EndInlineEdit(commit: true);
        GraphCanvas.Editor = workspace!.Editor;
        RefreshGraphList();
        RefreshPaletteForVariables();
        ShowInspector();
        ShowCompilation(workspace.LastCompilation);
        UpdateTitle();
        UpdateCommandStates();
    }

    private void OnGraphChanged(object? sender, EventArgs e)
    {
        // Recompiling on every pointer event would be wasteful; coalesce bursts of edits.
        recompileTimer.Stop();
        recompileTimer.Start();
        UpdateTitle();
        UpdateCommandStates();
        ShowInspector();
        RefreshPaletteForVariables();
        RefreshGraphList();
    }

    private void Recompile()
    {
        if (workspace is not null)
        {
            ShowCompilation(workspace.Compile());
        }
    }

    private bool TrySave()
    {
        if (workspace is null)
        {
            return false;
        }

        try
        {
            workspace.Save();
            UpdateTitle();
            SetStatus("Saved.");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReportError("Could not save", exception);
            return false;
        }
    }

    private void ShowCompilation(GraphCompilationResult result)
    {
        ShowProblems(result.Diagnostics);
        if (result.Source is { } source)
        {
            if (CodeEditor.Text != source.Text)
            {
                syncingCodeSelection = true;
                CodeEditor.Text = source.Text;
                syncingCodeSelection = false;
            }

            CodeBanner.Text = $"Generated/{source.RelativePath} — read-only. Edit the graph to change it; put handwritten C# in separate .cs files.";
        }
        else
        {
            CodeBanner.Text = "The graph has errors, so no code is generated. See Problems.";
        }

        HighlightSelectedNodeCode();
    }

    private void ShowProblems(IReadOnlyList<GraphDiagnostic> diagnostics)
    {
        ProblemsList.Items.Clear();
        foreach (GraphDiagnostic diagnostic in diagnostics.OrderByDescending(diagnostic => diagnostic.Severity))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new TextBlock
            {
                Text = diagnostic.Severity == DiagnosticSeverity.Error ? "Error" : diagnostic.Severity == DiagnosticSeverity.Warning ? "Warning" : "Info",
                Foreground = (IBrush)this.FindResource(diagnostic.Severity == DiagnosticSeverity.Error ? "IdeError" : "IdeWarning")!,
                FontWeight = FontWeight.SemiBold,
                Width = 60,
            });
            row.Children.Add(new TextBlock { Text = diagnostic.Id, Classes = { "secondary" }, Width = 64 });
            row.Children.Add(new TextBlock { Text = diagnostic.Message });
            if (diagnostic.FilePath is { } file)
            {
                row.Children.Add(new TextBlock { Text = $"{Path.GetFileName(file)}({diagnostic.Line})", Classes = { "secondary" } });
            }

            ProblemsList.Items.Add(new ListBoxItem { Content = row, Tag = diagnostic });
        }

        NoProblemsText.IsVisible = diagnostics.Count == 0;
        int errors = diagnostics.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        int warnings = diagnostics.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning);
        ProblemSummary.Text = errors == 0 && warnings == 0 ? "✓ No problems" : $"{errors} error(s), {warnings} warning(s)";
        ProblemSummary.Foreground = (IBrush)this.FindResource(errors > 0 ? "IdeError" : warnings > 0 ? "IdeWarning" : "IdeTextSecondary")!;

        GraphCanvas.NodeStates = diagnostics
            .Where(diagnostic => diagnostic.NodeId is not null)
            .GroupBy(diagnostic => diagnostic.NodeId!)
            .ToDictionary(group => group.Key, group => group.Max(diagnostic => diagnostic.Severity));
    }

    private void GoToDiagnostic(ListBoxItem? item)
    {
        if (item?.Tag is GraphDiagnostic { NodeId: { } nodeId })
        {
            GraphCanvas.Select(nodeId, frame: true);
            GraphCanvas.Focus();
        }
    }

    private void OnCanvasSelectionChanged()
    {
        ShowInspector();
        HighlightSelectedNodeCode();
        UpdateCommandStates();
    }

    /// <summary>Selects the generated lines of the selected node (node → code navigation).</summary>
    private void HighlightSelectedNodeCode()
    {
        if (workspace?.LastCompilation.Source is not { } source || GraphCanvas.SelectedItems.Count != 1)
        {
            return;
        }

        if (source.FindNode(GraphCanvas.SelectedItems.First()) is not { } entry)
        {
            return;
        }

        syncingCodeSelection = true;
        var document = CodeEditor.Document;
        int start = document.GetLineByNumber(entry.StartLine).Offset;
        var endLine = document.GetLineByNumber(Math.Min(entry.EndLine, document.LineCount));
        CodeEditor.Select(start, endLine.EndOffset - start);
        CodeEditor.ScrollToLine(entry.StartLine);
        syncingCodeSelection = false;
    }

    /// <summary>Selects the node that produced the line under the caret (code → node navigation).</summary>
    private void OnCodeCaretMoved()
    {
        if (syncingCodeSelection || workspace?.LastCompilation.Source is not GeneratedSource source)
        {
            return;
        }

        if (source.FindLine(CodeEditor.TextArea.Caret.Line) is { } entry && !GraphCanvas.SelectedItems.Contains(entry.NodeId))
        {
            GraphCanvas.Select(entry.NodeId, frame: true);
        }
    }

    private void ReportError(string context, Exception exception)
    {
        SetStatus($"{context}: {exception.Message}");
        AppendOutput($"{context}: {exception.Message}");
        BottomTabs.SelectedItem = OutputTab;
    }

    private void SetStatus(string message) => StatusText.Text = message;

    private void SetBusy(bool value, string? message)
    {
        busy = value;
        if (message is not null)
        {
            SetStatus(message);
        }

        UpdateCommandStates();
    }

    private void UpdateTitle()
    {
        Title = workspace is null
            ? "VisualStudioLikeIDE"
            : $"{workspace.Graph.Graph.Name}{(workspace.IsDirty ? " •" : "")} — {workspace.Project.Name} — VisualStudioLikeIDE";
    }

    private void UpdateCommandStates()
    {
        bool open = workspace is not null;
        bool idle = open && !busy && runningProgram is null;
        SaveButton.IsEnabled = SaveMenuItem.IsEnabled = open;
        AddFunctionButton.IsEnabled = AddFunctionMenuItem.IsEnabled = open;
        UndoButton.IsEnabled = UndoMenuItem.IsEnabled = open && workspace!.Editor.CanUndo;
        RedoButton.IsEnabled = RedoMenuItem.IsEnabled = open && workspace!.Editor.CanRedo;
        ToolTip.SetTip(UndoButton, workspace?.Editor.UndoDescription is { } undo ? $"Undo {undo} (Ctrl+Z)" : "Undo (Ctrl+Z)");
        ToolTip.SetTip(RedoButton, workspace?.Editor.RedoDescription is { } redo ? $"Redo {redo} (Ctrl+Y)" : "Redo (Ctrl+Y)");
        DeleteMenuItem.IsEnabled = open && GraphCanvas.SelectedItems.Count > 0;
        CutMenuItem.IsEnabled = CopyMenuItem.IsEnabled = DuplicateMenuItem.IsEnabled = DeleteMenuItem.IsEnabled;
        AddCommentMenuItem.IsEnabled = ArrangeMenuItem.IsEnabled = open;
        ConnectMenuItem.IsEnabled = SelectedNodes().Take(3).Count() == 2;
        PasteMenuItem.IsEnabled = open;
        SelectAllMenuItem.IsEnabled = FrameMenuItem.IsEnabled = open;
        GenerateButton.IsEnabled = GenerateMenuItem.IsEnabled = idle;
        BuildButton.IsEnabled = BuildMenuItem.IsEnabled = idle;
        RunButton.IsEnabled = RunMenuItem.IsEnabled = idle;
        StopButton.IsEnabled = StopMenuItem.IsEnabled = buildCancellation is not null || runningProgram is not null;
        SendInputButton.IsEnabled = InputBox.IsEnabled = runningProgram is not null;
    }

    /// <summary>AvaloniaEdit's built-in C# definition, recolored for the dark theme (its defaults target light backgrounds).</summary>
    private static IHighlightingDefinition DarkCSharpHighlighting()
    {
        IHighlightingDefinition definition = HighlightingManager.Instance.GetDefinition("C#");
        foreach (HighlightingColor color in definition.NamedHighlightingColors)
        {
            string hex = color.Name switch
            {
                var name when name.Contains("Comment", StringComparison.OrdinalIgnoreCase) => "#6A9955",
                var name when name.Contains("String", StringComparison.OrdinalIgnoreCase) || name.Contains("Char", StringComparison.OrdinalIgnoreCase) => "#CE9178",
                var name when name.Contains("Number", StringComparison.OrdinalIgnoreCase) => "#B5CEA8",
                var name when name.Contains("Method", StringComparison.OrdinalIgnoreCase) => "#DCDCAA",
                var name when name.Contains("Preprocessor", StringComparison.OrdinalIgnoreCase) => "#9B9B9B",
                _ => "#569CD6",
            };
            color.Foreground = new SimpleHighlightingBrush(Color.Parse(hex));
        }

        return definition;
    }

    /// <summary>Derives a valid C# project name from a folder name, for example "my app" becomes "my_app".</summary>
    private static string ProjectNameFromFolder(string folderName)
    {
        var name = new System.Text.StringBuilder();
        foreach (char character in folderName)
        {
            name.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
        }

        if (name.Length == 0 || char.IsDigit(name[0]))
        {
            name.Insert(0, "App");
        }

        return GraphCompiler.IsValidNamespace(name.ToString()) ? name.ToString() : "App" + name;
    }
}
