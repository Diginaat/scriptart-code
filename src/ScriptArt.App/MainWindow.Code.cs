using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Search;
using ScriptArt.Compiler;
using ScriptArt.Compiler.Diagnostics;
using ScriptArt.Compiler.Nodes;
using ScriptArt.NodeEditor;
using ScriptArt.ProjectSystem;

namespace ScriptArt.App;

/// <summary>
/// Code views. The Code tab at the bottom shows the open graph's generated code (read-only, synchronized with node selection).
/// Files open in the editor area as tabs beside the graphs (D-043): handwritten C# is checked with Roslyn as you type; the
/// <c>.csproj</c>, solutions, and other text files are edited as text; generated files and the project file open read-only. Edits are
/// saved with the project (Ctrl+S). Go to Definition (F12) works from nodes and from either code view.
/// </summary>
public partial class MainWindow
{
    private readonly TextDocument generatedDocument = new();
    private readonly DispatcherTimer checkTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

    // The file shown in the editor area; null while a graph is shown.
    private string? shownFile;

    // Roslyn diagnostics of handwritten files from the latest check, shown in Problems with the open graph's diagnostics.
    private IReadOnlyList<GraphDiagnostic> fileDiagnostics = [];
    private int checkVersion;
    private bool loadingCode;

    private void InitializeCodeView()
    {
        CodeEditor.Document = generatedDocument;
        SearchPanel.Install(CodeEditor);
        SearchPanel.Install(FileEditor);
        FileEditor.TextChanged += (_, _) =>
        {
            if (!loadingCode && shownFile is not null)
            {
                UpdateFileBanner();
                RefreshDocumentTabs();
                UpdateTitle();
                if (shownFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    checkTimer.Stop();
                    checkTimer.Start();
                }
            }
        };
        FileEditor.TextArea.Caret.PositionChanged += (_, _) => UpdateFileStatus();
        checkTimer.Tick += (_, _) =>
        {
            checkTimer.Stop();
            CheckHandwrittenCode();
        };
    }

    /// <summary>Shows the open graph's generated code in the Code tab at the bottom.</summary>
    private void ShowGeneratedCode()
    {
        BottomTabs.SelectedItem = CodeTab;
        HighlightSelectedNodeCode();
    }

    /// <summary>
    /// Opens a file of the project in the editor area, with the caret at <paramref name="line"/> and <paramref name="column"/> when
    /// given. Graphs open on the canvas instead, and files that are not text are refused.
    /// </summary>
    private void ShowCodeFile(string path, int? line = null, int column = 1)
    {
        if (workspace is null)
        {
            return;
        }

        string fullPath = Path.GetFullPath(path);
        ProjectFileKind kind = ProjectFiles.KindOf(workspace.Project, fullPath);
        if (kind == ProjectFileKind.Graph)
        {
            if (workspace.Project.Graphs.FirstOrDefault(graph => string.Equals(Path.GetFullPath(Path.Combine(workspace.Project.DirectoryPath, graph.RelativePath)), fullPath, StringComparison.OrdinalIgnoreCase)) is { } projectGraph)
            {
                OpenProjectGraph(projectGraph);
            }

            return;
        }

        if (kind == ProjectFileKind.Binary)
        {
            SetStatus($"{Path.GetFileName(fullPath)} is not a text file, so it does not open in the editor.");
            return;
        }

        TextDocument document;
        try
        {
            document = workspace.OpenCodeFile(fullPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReportError($"Could not open {Path.GetFileName(fullPath)}", exception);
            return;
        }

        EndInlineEdit(commit: true);
        HideActionMenu();
        shownFile = fullPath;
        loadingCode = true;
        FileEditor.Document = document;
        FileEditor.IsReadOnly = kind is ProjectFileKind.Generated or ProjectFileKind.ProjectFile;
        FileEditor.SyntaxHighlighting = HighlightingFor(fullPath);
        loadingCode = false;
        GraphView.IsVisible = false;
        FileView.IsVisible = true;
        FileOpenGraphButton.IsVisible = kind == ProjectFileKind.Generated && workspace.Project.GraphForGeneratedPath(fullPath) is not null;
        ShowFileDocument(fullPath);
        UpdateFileBanner();
        UpdateFileStatus();
        if (line is { } target && target <= document.LineCount)
        {
            DocumentLine documentLine = document.GetLineByNumber(target);
            FileEditor.CaretOffset = documentLine.Offset + Math.Min(Math.Max(column - 1, 0), documentLine.Length);
            Dispatcher.UIThread.Post(() => FileEditor.ScrollToLine(target), DispatcherPriority.Background);
        }

        Dispatcher.UIThread.Post(() => FileEditor.Focus(), DispatcherPriority.Background);
        RevealInExplorer(fullPath, focus: false);
        RefreshProjectPanel();
        UpdateTitle();
        UpdateCommandStates();
    }

    private void UpdateCodeBanner()
    {
        CodeBanner.Text = workspace?.LastCompilation.Source is { } source
            ? $"Generated/{source.RelativePath} — generated from {workspace.Graph.Graph.Name}, read-only. Edit the graph to change it; put handwritten C# in separate .cs files."
            : "The graph has errors, so no code is generated. See Problems.";
    }

    private void UpdateFileBanner()
    {
        if (shownFile is null || workspace is null)
        {
            return;
        }

        string name = Path.GetRelativePath(workspace.Project.DirectoryPath, shownFile);
        string dirty = workspace.IsCodeFileDirty(shownFile) ? " •" : "";
        FileBanner.Text = ProjectFiles.KindOf(workspace.Project, shownFile) switch
        {
            ProjectFileKind.Generated => $"{name} — generated from a graph, read-only. Edit the graph to change it.",
            ProjectFileKind.ProjectFile => $"{name} — the project file, read-only. The IDE keeps it up to date.",
            ProjectFileKind.CSharp => $"{name}{dirty} — handwritten C#, compiled with the generated code and checked as you type. Ctrl+S saves.",
            _ when string.Equals(shownFile, workspace.Project.CsprojPath, StringComparison.OrdinalIgnoreCase)
                => $"{name}{dirty} — the MSBuild project. Packages and references you add here become nodes once restored. Ctrl+S saves.",
            _ => $"{name}{dirty} — Ctrl+S saves.",
        };
    }

    private void UpdateFileStatus()
    {
        if (shownFile is not null)
        {
            AvaloniaEdit.Editing.Caret caret = FileEditor.TextArea.Caret;
            FileStatus.Text = $"Ln {caret.Line}, Col {caret.Column}   ·   {FileEditor.Document.LineCount} lines   ·   {LanguageOf(shownFile)}"
                + (FileEditor.IsReadOnly ? "   ·   read-only" : "");
        }
    }

    private static string LanguageOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "C#",
        ".csproj" or ".props" or ".targets" => "MSBuild",
        ".sln" => "Solution",
        ".slnx" or ".xml" or ".axaml" or ".xaml" or ".resx" or ".config" => "XML",
        ".json" or ".vside" => "JSON",
        ".md" => "Markdown",
        _ => "Plain text",
    };

    // AvaloniaEdit's definition for the file's language, recolored for the dark theme; project files are XML.
    private static IHighlightingDefinition? HighlightingFor(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".csproj" or ".props" or ".targets" or ".slnx" or ".axaml" or ".xaml" or ".resx" or ".config" => ".xml",
            ".vside" => ".json",
            var other => other,
        };
        return HighlightingManager.Instance.GetDefinitionByExtension(extension) is { } definition ? Recolored(definition) : null;
    }

    private void OnOpenFileGraph(object? sender, RoutedEventArgs e)
    {
        if (workspace is not null && shownFile is not null && workspace.Project.GraphForGeneratedPath(shownFile) is { } graph)
        {
            OpenProjectGraph(graph);
        }
    }

    private void OnFindInFile(object? sender, RoutedEventArgs e)
    {
        if (FileEditor.TextArea.GetService(typeof(SearchPanel)) is SearchPanel panel)
        {
            panel.Open();
        }
        else
        {
            FileEditor.Focus();
        }
    }

    private void OnOpenFileInCode(object? sender, RoutedEventArgs e)
    {
        if (workspace is not null && shownFile is not null)
        {
            OpenInCode(shownFile, FileEditor.TextArea.Caret.Line);
        }
    }

    private void OnRevealFile(object? sender, RoutedEventArgs e)
    {
        if (shownFile is not null)
        {
            RevealInExplorer(shownFile, focus: true);
        }
    }

    /// <summary>Checks the handwritten files, with their unsaved text, together with the generated code on a background thread.</summary>
    private async void CheckHandwrittenCode()
    {
        if (workspace is null)
        {
            return;
        }

        int version = ++checkVersion;
        VisualProject project = workspace.Project;
        VisualProject snapshot = workspace.Snapshot();
        GraphCompiler compiler = workspace.Compiler;
        IReadOnlyDictionary<string, string> unsaved = workspace.UnsavedFiles;
        HashSet<string> files = new(workspace.SourceFiles, StringComparer.OrdinalIgnoreCase);
        ProjectGenerationResult result;
        try
        {
            result = await Task.Run(() => snapshot.PreviewGeneration(compiler, unsaved));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (version == checkVersion && workspace?.Project == project)
        {
            fileDiagnostics = [.. result.Diagnostics.Where(diagnostic => diagnostic.FilePath is { } path && files.Contains(path))];
            ShowProblems(workspace.LastCompilation.Diagnostics);
        }
    }

    private void GoToFileDiagnostic(GraphDiagnostic diagnostic)
    {
        if (workspace is null || diagnostic.FilePath is not { } path)
        {
            return;
        }

        if (workspace.Project.GraphForGeneratedPath(path) is { } graph)
        {
            if (graph != workspace.Graph || shownFile is not null)
            {
                OpenProjectGraph(graph);
            }

            ShowGeneratedCode();
            if (diagnostic.Line is { } line && line <= generatedDocument.LineCount)
            {
                CodeEditor.ScrollToLine(line);
            }
        }
        else
        {
            ShowCodeFile(path, diagnostic.Line);
        }
    }

    private async void OnGoToDefinition(object? sender, RoutedEventArgs e)
    {
        if (FileEditor.IsKeyboardFocusWithin && shownFile is not null)
        {
            await GoToCodeDefinitionAsync(shownFile, FileEditor.CaretOffset);
        }
        else if (CodeEditor.IsKeyboardFocusWithin && workspace is not null)
        {
            await GoToCodeDefinitionAsync(workspace.Project.GeneratedPath(workspace.Graph.Graph), CodeEditor.CaretOffset);
        }
        else
        {
            GoToNodeDefinition();
        }
    }

    /// <summary>Opens the graph, variable, or type that defines the selected node.</summary>
    private void GoToNodeDefinition()
    {
        if (workspace is null || GraphCanvas.SelectedItems.Count != 1 || workspace.Editor.Graph.FindNode(GraphCanvas.SelectedItems.First()) is not { } node)
        {
            SetStatus("Select one node, or put the cursor on a name in the code, to go to its definition.");
            return;
        }

        if (workspace.Editor.NavigationTargetOf(node.Id) is not { } target)
        {
            NodeDefinition? definition = workspace.Editor.DefinitionOf(node);
            SetStatus(definition is ExternalMethodNode external
                ? $"{definition.DisplayName} is part of .NET ({external.Method.DeclaringType.Namespace}.{external.Method.DeclaringType.Name}); it has no graph or source in this project."
                : $"{definition?.DisplayName ?? "This node"} is built into the editor; there is no definition to open.");
            return;
        }

        if (workspace.Project.Graphs.FirstOrDefault(graph => graph.Graph.Id == target.GraphId) is { } defining && defining != workspace.Graph)
        {
            OpenProjectGraph(defining);
        }

        if (target.VariableId is { } variable)
        {
            LeftTabs.SelectedItem = MyProjectTab;
            SelectVariable(variable);
            SetStatus($"Variable {workspace.Editor.Graph.FindVariable(variable)?.Name} is shown in Details.");
        }
        else if (target.PropertyId is { } property)
        {
            SetStatus($"Property {workspace.Editor.Graph.Properties.Find(candidate => candidate.Id == property)?.Name} of {workspace.Editor.Graph.Name} is edited in Details.");
        }
    }

    /// <summary>Opens the declaration of the symbol at <paramref name="offset"/> in <paramref name="path"/>: a node of a graph, a handwritten file, or a message for .NET symbols.</summary>
    private async Task GoToCodeDefinitionAsync(string path, int offset)
    {
        if (workspace is null)
        {
            return;
        }

        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("Go to Definition works in C# files.");
            return;
        }

        VisualProject project = workspace.Project;
        VisualProject snapshot = workspace.Snapshot();
        GraphCompiler compiler = workspace.Compiler;
        IReadOnlyDictionary<string, string> unsaved = workspace.UnsavedFiles;
        SetStatus("Finding the definition…");
        ProjectDefinition? definition;
        try
        {
            definition = await Task.Run(() => snapshot.FindDefinition(compiler, path, offset, unsaved));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReportError("Could not find the definition", exception);
            return;
        }

        if (workspace?.Project != project)
        {
            return;
        }

        if (definition is null)
        {
            SetStatus("There is no name with a definition at the cursor.");
        }
        else if (definition.Graph is { } copy)
        {
            // The lookup ran on a snapshot; open the project's own graph, unless it was removed meanwhile.
            if (project.Graphs.FirstOrDefault(candidate => candidate.Graph.Id == copy.Graph.Id) is not { } graph)
            {
                SetStatus($"{definition.SymbolName} was defined in a graph that no longer exists.");
                return;
            }

            if (graph != workspace.Graph || shownFile is not null)
            {
                OpenProjectGraph(graph);
            }

            if (definition.NodeId is { } nodeId)
            {
                GraphCanvas.Select(nodeId, frame: true);
            }
            else
            {
                ShowGeneratedCode();
                if (definition.Line <= generatedDocument.LineCount)
                {
                    CodeEditor.ScrollToLine(definition.Line);
                }
            }

            SetStatus($"{definition.SymbolName} is defined in {graph.Graph.Name}.");
        }
        else if (definition.FilePath is { } file)
        {
            ShowCodeFile(file, definition.Line, definition.Column);
            SetStatus($"{definition.SymbolName} is defined in {Path.GetFileName(file)}, line {definition.Line}.");
        }
        else
        {
            SetStatus($"{definition.SymbolName} is defined in a referenced assembly, which has no source in this project.");
        }
    }

    /// <summary>AvaloniaEdit's built-in definitions, recolored for the current theme (their defaults target light backgrounds).</summary>
    private static IHighlightingDefinition Recolored(IHighlightingDefinition definition)
    {
        foreach (HighlightingColor color in definition.NamedHighlightingColors)
        {
            color.Foreground = new SimpleHighlightingBrush(Color.Parse(IdeColors.SyntaxColor(color.Name, Canvas.CanvasTheme.IsLight)));
        }

        return definition;
    }
}
