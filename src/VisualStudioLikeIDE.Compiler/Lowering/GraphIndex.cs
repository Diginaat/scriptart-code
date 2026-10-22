using VisualStudioLikeIDE.Compiler.Diagnostics;
using VisualStudioLikeIDE.Compiler.Nodes;
using VisualStudioLikeIDE.GraphModel;

namespace VisualStudioLikeIDE.Compiler.Lowering;

/// <summary>A graph node paired with its definition.</summary>
public sealed record ResolvedNode(GraphNode Node, NodeDefinition Definition)
{
    public string Id => Node.Id;
}

/// <summary>
/// A structurally validated, indexed view of a graph. Only valid nodes and connections are indexed,
/// so later passes never see dangling references or ill-typed links.
/// </summary>
public sealed class GraphIndex
{
    private readonly Dictionary<string, ResolvedNode> nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<PinRef, List<GraphConnection>> byOutput = [];
    private readonly Dictionary<PinRef, List<GraphConnection>> byInput = [];

    private GraphIndex(GraphDocument graph)
    {
        Graph = graph;
    }

    public GraphDocument Graph { get; }

    public IEnumerable<ResolvedNode> Nodes => nodes.Values;

    public ResolvedNode? Find(string nodeId) => nodes.GetValueOrDefault(nodeId);

    public IReadOnlyList<GraphConnection> ConnectionsFrom(PinRef output) =>
        byOutput.TryGetValue(output, out List<GraphConnection>? list) ? list : [];

    public IReadOnlyList<GraphConnection> ConnectionsTo(PinRef input) =>
        byInput.TryGetValue(input, out List<GraphConnection>? list) ? list : [];

    /// <summary>The node connected to an execution output, if any.</summary>
    public ResolvedNode? ExecutionTarget(string nodeId, string pinId)
    {
        IReadOnlyList<GraphConnection> connections = ConnectionsFrom(new PinRef(nodeId, pinId));
        return connections.Count == 0 ? null : nodes[connections[0].To.Node];
    }

    /// <summary>Nodes reached through the execution outputs of a node, in pin declaration order.</summary>
    public IEnumerable<ResolvedNode> ExecutionSuccessors(ResolvedNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        foreach (PinDefinition pin in node.Definition.Pins)
        {
            if (pin.IsExecution && pin.Direction == PinDirection.Output && ExecutionTarget(node.Id, pin.Id) is { } target)
            {
                yield return target;
            }
        }
    }

    /// <summary>
    /// Resolves node definitions and checks every connection. Problems are reported to <paramref name="diagnostics"/>
    /// and the offending elements are left out of the index.
    /// </summary>
    /// <param name="functions">The project's function graphs, which call nodes refer to.</param>
    public static GraphIndex Build(GraphDocument graph, NodeLibrary library, List<GraphDiagnostic> diagnostics, IReadOnlyList<GraphDocument>? functions = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var index = new GraphIndex(graph);
        foreach (GraphNode node in graph.Nodes)
        {
            index.AddNode(node, library, functions, diagnostics);
        }

        var seen = new HashSet<GraphConnection>();
        foreach (GraphConnection connection in graph.Connections)
        {
            if (!seen.Add(connection))
            {
                diagnostics.Add(new(DiagnosticIds.DuplicateConnection, DiagnosticSeverity.Warning,
                    "The same connection appears more than once and was ignored.", graph.Id, connection.To.Node, connection.To));
                continue;
            }

            index.AddConnection(connection, diagnostics);
        }

        return index;
    }

    private void AddNode(GraphNode node, NodeLibrary library, IReadOnlyList<GraphDocument>? functions, List<GraphDiagnostic> diagnostics)
    {
        if (nodes.ContainsKey(node.Id))
        {
            diagnostics.Add(new(DiagnosticIds.DuplicateNodeId, DiagnosticSeverity.Error,
                $"Node id '{node.Id}' is used more than once.", Graph.Id, node.Id));
            return;
        }

        NodeDefinition? definition = library.Resolve(node, Graph, functions);
        if (definition is null && node.Type is VariableNodes.GetTypeId or VariableNodes.SetTypeId)
        {
            diagnostics.Add(new(DiagnosticIds.UnknownVariable, DiagnosticSeverity.Error,
                "This node refers to a variable that no longer exists, or whose type is not supported.", Graph.Id, node.Id));
            return;
        }

        if (definition is null && node.Type is FunctionNodes.EntryTypeId or FunctionNodes.ReturnTypeId or FunctionNodes.CallTypeId)
        {
            diagnostics.Add(new(DiagnosticIds.UnknownFunction, DiagnosticSeverity.Error, node.Type == FunctionNodes.CallTypeId
                ? "This node calls a function that is no longer in the project, or whose signature uses an unsupported type."
                : "This node only belongs in a function graph, and Return Value only in a function that returns a value.", Graph.Id, node.Id));
            return;
        }

        if (definition is null)
        {
            diagnostics.Add(new(DiagnosticIds.UnknownNodeType, DiagnosticSeverity.Error,
                $"Unknown node type '{node.Type}'. The node library that defines it may be missing.", Graph.Id, node.Id));
            return;
        }

        foreach (PropertyDefinition property in definition.Properties)
        {
            string value = node.Properties.GetValueOrDefault(property.Id, property.DefaultValue);
            if (definition.ValidateProperty(property, value) is { } error)
            {
                diagnostics.Add(new(DiagnosticIds.InvalidPropertyValue, DiagnosticSeverity.Error,
                    $"{definition.DisplayName}: {property.DisplayName}: {error}", Graph.Id, node.Id));
            }
        }

        nodes.Add(node.Id, new ResolvedNode(node, definition));
    }

    private void AddConnection(GraphConnection connection, List<GraphDiagnostic> diagnostics)
    {
        PinDefinition? from = FindPin(connection.From);
        PinDefinition? to = FindPin(connection.To);
        if (from is null || to is null)
        {
            // Connections attached to unknown nodes are already covered by the node diagnostic.
            PinRef missing = from is null ? connection.From : connection.To;
            if (nodes.ContainsKey(missing.Node))
            {
                diagnostics.Add(new(DiagnosticIds.DanglingConnection, DiagnosticSeverity.Error,
                    $"A connection refers to pin '{missing.Pin}', which this node does not have.", Graph.Id, missing.Node, missing));
            }
            else if (!Graph.Nodes.Exists(node => node.Id == missing.Node))
            {
                diagnostics.Add(new(DiagnosticIds.DanglingConnection, DiagnosticSeverity.Error,
                    $"A connection refers to node '{missing.Node}', which does not exist.", Graph.Id, Pin: missing));
            }

            return;
        }

        ConnectionCheck check = ConnectionRules.Check(from, to);
        if (!check.IsAllowed)
        {
            diagnostics.Add(new(check.ErrorId!, DiagnosticSeverity.Error, check.Error!, Graph.Id, connection.To.Node, connection.To));
            return;
        }

        if (ConnectionRules.IsSingleConnection(to) && byInput.ContainsKey(connection.To))
        {
            diagnostics.Add(new(DiagnosticIds.MultipleInputConnections, DiagnosticSeverity.Error,
                $"Input '{to.DisplayName}' has more than one connection.", Graph.Id, connection.To.Node, connection.To));
            return;
        }

        if (ConnectionRules.IsSingleConnection(from) && byOutput.ContainsKey(connection.From))
        {
            diagnostics.Add(new(DiagnosticIds.MultipleExecutionTargets, DiagnosticSeverity.Error,
                "An execution output can lead to only one node. Use a Branch to choose between paths.", Graph.Id, connection.From.Node, connection.From));
            return;
        }

        Add(byOutput, connection.From, connection);
        Add(byInput, connection.To, connection);
    }

    private PinDefinition? FindPin(PinRef pin) => nodes.GetValueOrDefault(pin.Node)?.Definition.FindPin(pin.Pin);

    private static void Add(Dictionary<PinRef, List<GraphConnection>> map, PinRef key, GraphConnection connection)
    {
        if (!map.TryGetValue(key, out List<GraphConnection>? list))
        {
            list = [];
            map.Add(key, list);
        }

        list.Add(connection);
    }
}
