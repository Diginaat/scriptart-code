using VisualStudioLikeIDE.Compiler;
using VisualStudioLikeIDE.Compiler.Nodes;
using VisualStudioLikeIDE.NodeEditor;
using VisualStudioLikeIDE.ProjectSystem;

namespace VisualStudioLikeIDE.App;

/// <summary>
/// The open project and the graph being edited. Phase 1 projects contain one entry graph, which is opened on load.
/// </summary>
internal sealed class Workspace
{
    private Workspace(VisualProject project, GraphCompiler compiler)
    {
        Project = project;
        Compiler = compiler;
        Graph = project.Graphs[0];
        Editor = new GraphEditor(Graph.Graph, compiler.Library);
        LastCompilation = Compile();
    }

    public VisualProject Project { get; }

    public GraphCompiler Compiler { get; }

    public ProjectGraph Graph { get; }

    public GraphEditor Editor { get; }

    /// <summary>The latest in-memory compilation of the open graph. Updated by <see cref="Compile"/>.</summary>
    public GraphCompilationResult LastCompilation { get; private set; }

    public static Workspace Open(VisualProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Graphs.Count == 0)
        {
            throw new InvalidOperationException($"Project '{project.Name}' contains no graphs.");
        }

        return new Workspace(project, new GraphCompiler(NodeLibrary.CreateDefault()));
    }

    /// <summary>Compiles the open graph in memory. Nothing is written to disk.</summary>
    public GraphCompilationResult Compile()
    {
        LastCompilation = Compiler.Compile(Graph.Graph, new GraphCompilationOptions(Project.Document.RootNamespace), Graph.RelativePath);
        return LastCompilation;
    }

    public void Save()
    {
        Project.Save();
        Editor.MarkSaved();
    }
}
