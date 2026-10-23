using ScriptArt.Compiler.Diagnostics;
using ScriptArt.Compiler.Nodes;
using ScriptArt.Compiler.TypeSystem;
using ScriptArt.GraphModel;

namespace ScriptArt.Compiler.Lowering;

/// <summary>
/// A structurally validated, indexed view of a graph. Only valid nodes and connections are indexed,
/// so later passes never see dangling references or ill-typed links.
/// </summary>
public sealed class GraphIndex
{
    private readonly Dictionary<string, ResolvedNode> nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<PinRef, List<GraphConnection>> byOutput = [];
    private readonly Dictionary<PinRef, List<GraphConnection>> byInput = [];

    private GraphIndex(GraphDocument graph, IReadOnlyList<GraphDocument> projectGraphs)
    {
        Graph = graph;
        ProjectGraphs = projectGraphs;
    }

    public GraphDocument Graph { get; }

    /// <summary>The project's graphs: the functions that call nodes refer to and the classes and structs that types name.</summary>
    public IReadOnlyList<GraphDocument> ProjectGraphs { get; }

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
    /// <param name="projectGraphs">The project's graphs, which call nodes, type nodes, and type names refer to.</param>
    public static GraphIndex Build(GraphDocument graph, NodeLibrary library, List<GraphDiagnostic> diagnostics, IReadOnlyList<GraphDocument>? projectGraphs = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var index = new GraphIndex(graph, projectGraphs ?? []);
        foreach (GraphNode node in graph.Nodes)
        {
            index.AddNode(node, library, diagnostics);
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

    private void AddNode(GraphNode node, NodeLibrary library, List<GraphDiagnostic> diagnostics)
    {
        if (nodes.ContainsKey(node.Id))
        {
            diagnostics.Add(new(DiagnosticIds.DuplicateNodeId, DiagnosticSeverity.Error,
                $"Node id '{node.Id}' is used more than once.", Graph.Id, node.Id));
            return;
        }

        NodeDefinition? definition = library.Resolve(node, Graph, ProjectGraphs);
        if (definition is null && node.Type is VariableNodes.GetTypeId or VariableNodes.SetTypeId)
        {
            diagnostics.Add(new(DiagnosticIds.UnknownVariable, DiagnosticSeverity.Error,
                "This node refers to a variable that no longer exists, or whose type is not supported.", Graph.Id, node.Id));
            return;
        }

        if (definition is null && node.Type is FunctionNodes.EntryTypeId or FunctionNodes.ReturnTypeId or FunctionNodes.CallTypeId)
        {
            string? called = node.Properties.GetValueOrDefault(FunctionNodes.FunctionProperty);
            diagnostics.Add(new(DiagnosticIds.UnknownFunction, DiagnosticSeverity.Error, node.Type == FunctionNodes.CallTypeId
                ? TypeArgumentProblem(node, ProjectGraphs.FirstOrDefault(graph => graph.Id == called)?.Owner)
                    ?? "This node calls a function that is no longer in the project, or whose signature uses an unsupported type."
                : "This node only belongs in a function graph, and Return Value only in a function that returns a value.", Graph.Id, node.Id));
            return;
        }

        if (definition is null && TypeNodes.IsTypeNode(node.Type))
        {
            diagnostics.Add(new(DiagnosticIds.UnknownType, DiagnosticSeverity.Error,
                TypeArgumentProblem(node, node.Properties.GetValueOrDefault(TypeNodes.TypeProperty))
                    ?? "This node refers to a class, struct, or property that is no longer in the project, or whose type is not supported.", Graph.Id, node.Id));
            return;
        }

        if (definition is null && CollectionNodes.IsGeneratedNode(node.Type))
        {
            diagnostics.Add(new(DiagnosticIds.UnknownType, DiagnosticSeverity.Error,
                "This node works on a list or dictionary type that is not supported, or whose class or struct is no longer in the project.", Graph.Id, node.Id));
            return;
        }

        if (definition is null && library.IsGenericMethod(node.Type))
        {
            diagnostics.Add(new(DiagnosticIds.UnknownType, DiagnosticSeverity.Error,
                "This node's Type Arguments are not types the method can use. Write one type per type parameter, such as int.", Graph.Id, node.Id));
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

    // A node of a generic class or struct, or a call of one of its methods, names one type argument per type parameter.
    private string? TypeArgumentProblem(GraphNode node, string? typeGraphId)
    {
        if (ProjectGraphs.FirstOrDefault(graph => graph.Id == typeGraphId) is not { TypeParameters.Count: > 0 } type
            || TypeRef.ArgumentsFromNames(node.Properties.GetValueOrDefault(TypeNodes.TypeArgumentsProperty), ProjectGraphs, Graph)?.Count == type.TypeParameters.Count)
        {
            return null;
        }

        return $"{type.Name} is generic, and this node does not give a valid type argument for each of {string.Join(", ", type.TypeParameters)}. "
            + $"Replace it with the node for a {type.Name}<…> type from the palette.";
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
