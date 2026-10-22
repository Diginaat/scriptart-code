using ScriptArt.Compiler.Emit;

namespace ScriptArt.ProjectSystem.Debugging;

/// <summary>A breakpoint on a node: the program stops before the first statement the node generated.</summary>
public sealed record NodeBreakpoint(string GraphId, string NodeId);

/// <summary>A line of a C# file. Lines are one-based.</summary>
public sealed record SourceLocation(string Path, int Line);

/// <summary>
/// Maps between graph nodes and the lines of the generated files a debugger works with, through the source maps of the code that
/// was last generated (<see cref="ProjectGenerationResult.Sources"/>).
/// </summary>
public sealed class DebugSourceMap
{
    private readonly VisualProject project;
    private readonly Dictionary<string, GeneratedSource> sourcesByPath = new(StringComparer.OrdinalIgnoreCase);

    public DebugSourceMap(VisualProject project, IReadOnlyList<GeneratedSource> sources)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sources);
        this.project = project;
        foreach (GeneratedSource source in sources)
        {
            sourcesByPath[Path.GetFullPath(Path.Combine(project.GeneratedDirectory, source.RelativePath))] = source;
        }
    }

    /// <summary>
    /// The first line generated for the breakpoint's node; null when the graph or node is gone, or the node generated no statement
    /// of its own (a value inlined into the statement that uses it).
    /// </summary>
    public SourceLocation? LocationOf(NodeBreakpoint breakpoint)
    {
        ArgumentNullException.ThrowIfNull(breakpoint);
        if (project.Graphs.FirstOrDefault(graph => graph.Graph.Id == breakpoint.GraphId) is not { } graph)
        {
            return null;
        }

        string path = Path.GetFullPath(project.GeneratedPath(graph.Graph));
        return sourcesByPath.TryGetValue(path, out GeneratedSource? source) && source.FindNode(breakpoint.NodeId) is { } entry
            ? new SourceLocation(path, entry.StartLine)
            : null;
    }

    /// <summary>The lines to set breakpoints on, grouped by file, for every breakpoint that has a line.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<int>> Lines(IEnumerable<NodeBreakpoint> breakpoints) =>
        breakpoints.Select(LocationOf).OfType<SourceLocation>()
            .GroupBy(location => location.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<int>)[.. group.Select(location => location.Line).Distinct().Order()], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The graph whose generated file is <paramref name="path"/> and the innermost node whose code contains
    /// <paramref name="line"/>; null for a file no graph generated, such as handwritten code.
    /// </summary>
    public (ProjectGraph Graph, string? NodeId)? NodeAt(string path, int line)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (project.GraphForGeneratedPath(path) is not { } graph)
        {
            return null;
        }

        string? nodeId = sourcesByPath.TryGetValue(Path.GetFullPath(path), out GeneratedSource? source) ? source.FindLine(line)?.NodeId : null;
        return (graph, nodeId);
    }
}
