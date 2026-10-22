using System.Collections.Concurrent;
using VisualStudioLikeIDE.Compiler.TypeSystem;
using VisualStudioLikeIDE.GraphModel;

namespace VisualStudioLikeIDE.Compiler.Nodes;

/// <summary>
/// The set of node definitions available to a project, keyed by <see cref="NodeDefinition.TypeId"/>.
/// </summary>
public sealed class NodeLibrary
{
    private readonly Dictionary<string, NodeDefinition> definitions = new(StringComparer.Ordinal);

    // Variable nodes get their pins from the variable they refer to. Definitions are immutable, so one is shared per variable state.
    private readonly ConcurrentDictionary<(string Id, string Name, TypeRef Type, bool Set), NodeDefinition> variableDefinitions = new();

    // Function entry, return, and call nodes get their pins from a function's signature; keyed by node type and signature.
    private readonly ConcurrentDictionary<string, NodeDefinition> functionDefinitions = new(StringComparer.Ordinal);

    public IEnumerable<NodeDefinition> Definitions => definitions.Values;

    public void Register(NodeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!definitions.TryAdd(definition.TypeId, definition))
        {
            throw new InvalidOperationException($"A node definition with type '{definition.TypeId}' is already registered.");
        }
    }

    public NodeDefinition? Find(string typeId) => definitions.GetValueOrDefault(typeId);

    /// <summary>
    /// The definition of a node placed in <paramref name="graph"/>. Most nodes are found by type. Variable nodes are built from the
    /// variable named by their <see cref="VariableNodes.VariableProperty"/>, and function nodes from a function's signature: the
    /// graph's own for entry and return nodes, the called graph's (found in <paramref name="functions"/>) for call nodes. These are
    /// null when what they refer to is missing, has an unsupported type, or does not fit the graph.
    /// </summary>
    public NodeDefinition? Resolve(GraphNode node, GraphDocument graph, IReadOnlyList<GraphDocument>? functions = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(graph);
        switch (node.Type)
        {
            case VariableNodes.GetTypeId or VariableNodes.SetTypeId:
                return node.Properties.TryGetValue(VariableNodes.VariableProperty, out string? variableId) && graph.FindVariable(variableId) is { } variable
                    ? VariableDefinition(variable, node.Type == VariableNodes.SetTypeId)
                    : null;
            case FunctionNodes.EntryTypeId:
                return FunctionSignature.Of(graph) is { } own ? FunctionDefinition(node.Type, own) : null;
            case FunctionNodes.ReturnTypeId:
                return FunctionSignature.Of(graph) is { } returning && returning.ReturnType != TypeRef.Void ? FunctionDefinition(node.Type, returning) : null;
            case FunctionNodes.CallTypeId:
                return node.Properties.TryGetValue(FunctionNodes.FunctionProperty, out string? functionId)
                    && functions?.FirstOrDefault(function => function.Id == functionId) is { } called
                    && FunctionSignature.Of(called) is { } signature
                    ? FunctionDefinition(node.Type, signature)
                    : null;
            default:
                return Find(node.Type);
        }
    }

    /// <summary>
    /// Every node that can be added to <paramref name="graph"/>: the registered nodes, a Get and a Set node per variable, the entry
    /// and return nodes of a function graph, and a call node per function in <paramref name="functions"/>.
    /// </summary>
    public IEnumerable<NodeDefinition> DefinitionsFor(GraphDocument graph, IReadOnlyList<GraphDocument>? functions = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        IEnumerable<NodeDefinition?> graphNodes = graph.Variables.SelectMany(variable =>
            new[] { VariableDefinition(variable, set: false), VariableDefinition(variable, set: true) });
        if (FunctionSignature.Of(graph) is { } own)
        {
            graphNodes = graphNodes.Append(FunctionDefinition(FunctionNodes.EntryTypeId, own));
            if (own.ReturnType != TypeRef.Void)
            {
                graphNodes = graphNodes.Append(FunctionDefinition(FunctionNodes.ReturnTypeId, own));
            }
        }

        IEnumerable<NodeDefinition?> calls = (functions ?? []).Select(FunctionSignature.Of).OfType<FunctionSignature>()
            .Select(signature => FunctionDefinition(FunctionNodes.CallTypeId, signature));
        return Definitions.Concat(graphNodes.Concat(calls).OfType<NodeDefinition>());
    }

    private NodeDefinition FunctionDefinition(string typeId, FunctionSignature signature) =>
        functionDefinitions.GetOrAdd(typeId + "\n" + signature.Key, _ => typeId switch
        {
            FunctionNodes.EntryTypeId => new FunctionEntryNode(signature),
            FunctionNodes.ReturnTypeId => new FunctionReturnNode(signature.ReturnType),
            _ => new FunctionCallNode(signature),
        });

    private NodeDefinition? VariableDefinition(GraphVariable variable, bool set) =>
        TypeRef.FromName(variable.Type) is { } type
            ? variableDefinitions.GetOrAdd((variable.Id, variable.Name, type, set), key => key.Set
                ? new VariableSetNode(key.Id, key.Name, key.Type)
                : new VariableGetNode(key.Id, key.Name, key.Type))
            : null;

    /// <summary>
    /// Creates a library containing every built-in node, a node per callable method of <see cref="ExternalApi.FrameworkTypes"/>,
    /// and a node per callable method of the assemblies at <paramref name="referencePaths"/>, such as a project's packages.
    /// </summary>
    public static NodeLibrary CreateDefault(IReadOnlyList<string>? referencePaths = null)
    {
        var library = new NodeLibrary();
        foreach (NodeDefinition definition in BuiltInNodes.All())
        {
            library.Register(definition);
        }

        library.RegisterExternalMethods(ExternalApi.Framework);
        library.RegisterExternalMethods(ExternalApi.FromAssemblies(referencePaths ?? []));
        return library;
    }

    /// <summary>
    /// Adds a node per method, for example after a restore made a package's assemblies known. Methods already registered are
    /// skipped, so a package that forwards a type to the framework adds nothing twice.
    /// </summary>
    public void RegisterExternalMethods(IEnumerable<ExternalMethod> methods)
    {
        ArgumentNullException.ThrowIfNull(methods);
        foreach (ExternalMethod method in methods)
        {
            if (Find(method.TypeId) is null)
            {
                Register(ExternalApi.CreateNode(method));
            }
        }
    }
}
