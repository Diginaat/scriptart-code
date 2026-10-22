using VisualStudioLikeIDE.Compiler;
using VisualStudioLikeIDE.Compiler.Nodes;
using VisualStudioLikeIDE.GraphModel;
using VisualStudioLikeIDE.NodeEditor;
using VisualStudioLikeIDE.ProjectSystem;

namespace VisualStudioLikeIDE.App;

/// <summary>
/// The open project and the graph being edited. Each graph keeps its own editor, so undo history survives switching graphs.
/// The entry graph is opened on load.
/// </summary>
internal sealed class Workspace
{
    private readonly Dictionary<GraphDocument, GraphEditor> editors = [];

    // Adding a function changes the project file, which no graph editor's undo history covers.
    private bool projectChanged;

    private Workspace(VisualProject project, GraphCompiler compiler)
    {
        Project = project;
        Compiler = compiler;
        Graph = project.Graphs.FirstOrDefault(graph => graph.Graph.Kind == GraphKind.EntryPoint) ?? project.Graphs[0];
        Editor = EditorFor(Graph);
        LastCompilation = Compile();
    }

    public VisualProject Project { get; }

    public GraphCompiler Compiler { get; }

    public ProjectGraph Graph { get; private set; }

    public GraphEditor Editor { get; private set; }

    /// <summary>True when a graph or the project file has changes that are not saved.</summary>
    public bool IsDirty => projectChanged || editors.Values.Any(editor => editor.IsDirty);

    /// <summary>The latest in-memory compilation of the open graph. Updated by <see cref="Compile"/>.</summary>
    public GraphCompilationResult LastCompilation { get; private set; }

    /// <summary>Raised when any graph's editor changes, including graphs that are not open.</summary>
    public event EventHandler? Changed;

    public static Workspace Open(VisualProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Graphs.Count == 0)
        {
            throw new InvalidOperationException($"Project '{project.Name}' contains no graphs.");
        }

        return new Workspace(project, new GraphCompiler(NodeLibrary.CreateDefault()));
    }

    /// <summary>Makes <paramref name="graph"/> the graph being edited and compiles it.</summary>
    public void OpenGraph(ProjectGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        Graph = graph;
        Editor = EditorFor(graph);
        Compile();
    }

    /// <summary>Adds a function graph with an unused name such as <c>Function1</c> and opens it.</summary>
    public ProjectGraph AddFunction()
    {
        string name = Enumerable.Range(1, int.MaxValue)
            .Select(number => "Function" + number.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .First(candidate => !Project.Graphs.Any(graph =>
                string.Equals(graph.Graph.Name, candidate, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(graph.RelativePath)), candidate, StringComparison.OrdinalIgnoreCase)));
        ProjectGraph added = Project.AddFunction(name);
        projectChanged = true;
        IReadOnlyList<GraphDocument> functions = Project.Functions;
        foreach (GraphEditor editor in editors.Values)
        {
            editor.Functions = functions;
        }

        OpenGraph(added);
        return added;
    }

    /// <summary>Compiles the open graph in memory. Nothing is written to disk.</summary>
    public GraphCompilationResult Compile()
    {
        LastCompilation = Compiler.Compile(Graph.Graph, Project.CompilationOptions, Graph.RelativePath);
        return LastCompilation;
    }

    public void Save()
    {
        Project.Save();
        projectChanged = false;
        foreach (GraphEditor editor in editors.Values)
        {
            editor.MarkSaved();
        }
    }

    private GraphEditor EditorFor(ProjectGraph graph)
    {
        if (!editors.TryGetValue(graph.Graph, out GraphEditor? editor))
        {
            editor = new GraphEditor(graph.Graph, Compiler.Library) { Functions = Project.Functions };
            editor.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
            editors.Add(graph.Graph, editor);
        }

        return editor;
    }
}
