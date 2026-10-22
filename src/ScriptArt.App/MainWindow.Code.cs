using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using ScriptArt.Compiler;
using ScriptArt.Compiler.Diagnostics;
using ScriptArt.Compiler.Nodes;
using ScriptArt.NodeEditor;
using ScriptArt.ProjectSystem;

namespace ScriptArt.App;

/// <summary>
/// The Code tab: the open graph's generated code (read-only, synchronized with node selection) or a handwritten C# file, which is
/// edited in place, saved with the project, and checked with Roslyn as you type. Go to Definition (F12) works from nodes and from
/// either kind of code.
/// </summary>
public partial class MainWindow
{
    private readonly TextDocument generatedDocument = new();
    private readonly DispatcherTimer checkTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

    // The handwritten file shown in the Code tab; null while it shows generated code.
    private string? shownFile;

    // Roslyn diagnostics of handwritten files from the latest check, shown in Problems with the open graph's diagnostics.
    private IReadOnlyList<GraphDiagnostic> fileDiagnostics = [];
    private int checkVersion;
    private bool loadingCode;

    private void InitializeCodeView()
    {
        CodeEditor.Document = generatedDocument;
        CodeEditor.TextChanged += (_, _) =>
        {
            if (!loadingCode && shownFile is not null)
            {
                UpdateCodeBanner();
                UpdateTitle();
                checkTimer.Stop();
                checkTimer.Start();
            }
        };
        checkTimer.Tick += (_, _) =>
        {
            checkTimer.Stop();
            CheckHandwrittenCode();
        };
    }

    private void OnShowGeneratedCode(object? sender, RoutedEventArgs e) => ShowGeneratedCode();

    /// <summary>Shows the open graph's generated code in the Code tab.</summary>
    private void ShowGeneratedCode()
    {
        if (shownFile is null)
        {
            return;
        }

        shownFile = null;
        loadingCode = true;
        CodeEditor.Document = generatedDocument;
        CodeEditor.IsReadOnly = true;
        loadingCode = false;
        UpdateCodeBanner();
        HighlightSelectedNodeCode();
        RefreshProjectPanel();
    }

    /// <summary>Opens a handwritten file in the Code tab, with the caret at <paramref name="line"/> and <paramref name="column"/> when given.</summary>
    private void ShowCodeFile(string path, int? line = null, int column = 1)
    {
        if (workspace is null)
        {
            return;
        }

        TextDocument document;
        try
        {
            document = workspace.OpenCodeFile(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReportError($"Could not open {Path.GetFileName(path)}", exception);
            return;
        }

        shownFile = Path.GetFullPath(path);
        loadingCode = true;
        CodeEditor.Document = document;
        CodeEditor.IsReadOnly = false;
        loadingCode = false;
        UpdateCodeBanner();
        BottomTabs.SelectedItem = CodeTab;
        if (line is { } target && target <= document.LineCount)
        {
            DocumentLine documentLine = document.GetLineByNumber(target);
            CodeEditor.CaretOffset = documentLine.Offset + Math.Min(Math.Max(column - 1, 0), documentLine.Length);
            CodeEditor.ScrollToLine(target);
        }

        CodeEditor.Focus();
        RefreshProjectPanel();
    }

    private void UpdateCodeBanner()
    {
        ShowGeneratedButton.IsVisible = shownFile is not null;
        if (shownFile is not null && workspace is not null)
        {
            string name = Path.GetRelativePath(workspace.Project.DirectoryPath, shownFile);
            CodeBanner.Text = $"{name}{(workspace.IsCodeFileDirty(shownFile) ? " •" : "")} — handwritten C#, saved with the project (Ctrl+S). F12 goes to a definition.";
        }
        else if (workspace?.LastCompilation.Source is { } source)
        {
            CodeBanner.Text = $"Generated/{source.RelativePath} — read-only. Edit the graph to change it; put handwritten C# in separate .cs files.";
        }
        else
        {
            CodeBanner.Text = "The graph has errors, so no code is generated. See Problems.";
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
        GraphCompiler compiler = workspace.Compiler;
        IReadOnlyDictionary<string, string> unsaved = workspace.UnsavedFiles;
        HashSet<string> files = new(workspace.SourceFiles, StringComparer.OrdinalIgnoreCase);
        ProjectGenerationResult result;
        try
        {
            result = await Task.Run(() => project.PreviewGeneration(compiler, unsaved));
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
            if (graph != workspace.Graph)
            {
                OpenProjectGraph(graph);
            }

            ShowGeneratedCode();
            BottomTabs.SelectedItem = CodeTab;
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
        if (CodeEditor.IsKeyboardFocusWithin)
        {
            await GoToCodeDefinitionAsync();
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
            LeftTabs.SelectedIndex = 0;
            SelectVariable(variable);
            SetStatus($"Variable {workspace.Editor.Graph.FindVariable(variable)?.Name} is shown in Details.");
        }
        else if (target.PropertyId is { } property)
        {
            SetStatus($"Property {workspace.Editor.Graph.Properties.Find(candidate => candidate.Id == property)?.Name} of {workspace.Editor.Graph.Name} is edited in Details.");
        }
    }

    /// <summary>Opens the declaration of the symbol under the caret: a node of a graph, a handwritten file, or a message for .NET symbols.</summary>
    private async Task GoToCodeDefinitionAsync()
    {
        if (workspace is null)
        {
            return;
        }

        VisualProject project = workspace.Project;
        GraphCompiler compiler = workspace.Compiler;
        string path = shownFile ?? project.GeneratedPath(workspace.Graph.Graph);
        int offset = CodeEditor.CaretOffset;
        IReadOnlyDictionary<string, string> unsaved = workspace.UnsavedFiles;
        SetStatus("Finding the definition…");
        ProjectDefinition? definition;
        try
        {
            definition = await Task.Run(() => project.FindDefinition(compiler, path, offset, unsaved));
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
        else if (definition.Graph is { } graph)
        {
            if (graph != workspace.Graph)
            {
                OpenProjectGraph(graph);
            }

            ShowGeneratedCode();
            if (definition.NodeId is { } nodeId)
            {
                GraphCanvas.Select(nodeId, frame: true);
            }
            else if (definition.Line <= generatedDocument.LineCount)
            {
                CodeEditor.ScrollToLine(definition.Line);
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
}
