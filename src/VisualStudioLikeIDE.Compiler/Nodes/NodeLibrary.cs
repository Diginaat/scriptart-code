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

    // Function entry, return, and call nodes get their pins from a function's signature, and type nodes from a class or struct;
    // keyed by node type and everything the pins depend on.
    private readonly ConcurrentDictionary<string, NodeDefinition> generatedDefinitions = new(StringComparer.Ordinal);

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
    /// variable named by their <see cref="VariableNodes.VariableProperty"/>, function nodes from a function's signature (the graph's
    /// own for entry and return nodes, the called graph's for call nodes), and type nodes from a class or struct. Called functions
    /// and types are found in <paramref name="projectGraphs"/>, the project's graphs. These are null when what they refer to is
    /// missing, has an unsupported type, or does not fit the graph.
    /// </summary>
    public NodeDefinition? Resolve(GraphNode node, GraphDocument graph, IReadOnlyList<GraphDocument>? projectGraphs = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(graph);
        projectGraphs ??= [];
        switch (node.Type)
        {
            case VariableNodes.GetTypeId or VariableNodes.SetTypeId:
                return node.Properties.TryGetValue(VariableNodes.VariableProperty, out string? variableId) && graph.FindVariable(variableId) is { } variable
                    ? VariableDefinition(variable, node.Type == VariableNodes.SetTypeId, projectGraphs)
                    : null;
            case FunctionNodes.EntryTypeId:
                return FunctionSignature.Of(graph, projectGraphs) is { } own ? FunctionDefinition(node.Type, own) : null;
            case FunctionNodes.ReturnTypeId:
                return FunctionSignature.Of(graph, projectGraphs) is { } returning && returning.ReturnType != TypeRef.Void ? FunctionDefinition(node.Type, returning) : null;
            case FunctionNodes.CallTypeId:
                return node.Properties.TryGetValue(FunctionNodes.FunctionProperty, out string? functionId)
                    && projectGraphs.FirstOrDefault(function => function.Id == functionId) is { } called
                    && FunctionSignature.Of(called, projectGraphs) is { } signature
                    ? FunctionDefinition(node.Type, signature)
                    : null;
            case TypeNodes.CreateTypeId or TypeNodes.GetTypeId or TypeNodes.SetTypeId or TypeNodes.WithTypeId:
                return node.Properties.TryGetValue(TypeNodes.TypeProperty, out string? typeId)
                    && projectGraphs.FirstOrDefault(type => type.Id == typeId) is { } typeGraph
                    && ProjectTypeDefinition.Of(typeGraph, projectGraphs) is { } definition
                    ? TypeDefinition(node.Type, definition, node.Properties.GetValueOrDefault(TypeNodes.PropertyProperty))
                    : null;
            default:
                return Find(node.Type);
        }
    }

    /// <summary>
    /// Every node that can be added to <paramref name="graph"/>: the registered nodes, a Get and a Set node per variable, the entry
    /// and return nodes of a function graph, a call node per function in <paramref name="projectGraphs"/>, and per class or struct a
    /// New node and nodes for its properties. A class or struct graph has no method, so nothing can be added to it.
    /// </summary>
    public IEnumerable<NodeDefinition> DefinitionsFor(GraphDocument graph, IReadOnlyList<GraphDocument>? projectGraphs = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        projectGraphs ??= [];
        if (TypeRef.IsTypeGraph(graph))
        {
            return [];
        }

        IEnumerable<NodeDefinition?> graphNodes = graph.Variables.SelectMany(variable =>
            new[] { VariableDefinition(variable, set: false, projectGraphs), VariableDefinition(variable, set: true, projectGraphs) });
        if (FunctionSignature.Of(graph, projectGraphs) is { } own)
        {
            graphNodes = graphNodes.Append(FunctionDefinition(FunctionNodes.EntryTypeId, own));
            if (own.ReturnType != TypeRef.Void)
            {
                graphNodes = graphNodes.Append(FunctionDefinition(FunctionNodes.ReturnTypeId, own));
            }
        }

        IEnumerable<NodeDefinition?> calls = projectGraphs.Select(function => FunctionSignature.Of(function, projectGraphs)).OfType<FunctionSignature>()
            .Select(signature => FunctionDefinition(FunctionNodes.CallTypeId, signature));
        IEnumerable<NodeDefinition?> types = projectGraphs.Select(type => ProjectTypeDefinition.Of(type, projectGraphs)).OfType<ProjectTypeDefinition>()
            .SelectMany(type => type.Properties
                .SelectMany(property => new[] { TypeNodes.GetTypeId, type.IsStruct ? TypeNodes.WithTypeId : TypeNodes.SetTypeId }
                    .Select(typeId => TypeDefinition(typeId, type, property.Id)))
                .Prepend(TypeDefinition(TypeNodes.CreateTypeId, type, null)));
        return Definitions.Concat(graphNodes.Concat(calls).Concat(types).OfType<NodeDefinition>());
    }

    private NodeDefinition FunctionDefinition(string typeId, FunctionSignature signature) =>
        generatedDefinitions.GetOrAdd(typeId + "\n" + signature.Key, _ => typeId switch
        {
            FunctionNodes.EntryTypeId => new FunctionEntryNode(signature),
            FunctionNodes.ReturnTypeId => new FunctionReturnNode(signature.ReturnType),
            _ => new FunctionCallNode(signature),
        });

    /// <summary>
    /// A type node of <paramref name="type"/>; property nodes also name the property. Null when the property is missing, and for Set
    /// on a struct or With on a class: a struct property is changed by copying the value, a class property by assigning it.
    /// </summary>
    private NodeDefinition? TypeDefinition(string typeId, ProjectTypeDefinition type, string? propertyId)
    {
        if (typeId == TypeNodes.CreateTypeId)
        {
            return generatedDefinitions.GetOrAdd(typeId + "\n" + type.Key, _ => type.IsStruct ? new CreateStructNode(type) : new CreateObjectNode(type));
        }

        int index = type.Properties.Select(candidate => candidate.Id).ToList().IndexOf(propertyId ?? "");
        if (index < 0 || (typeId == TypeNodes.SetTypeId && type.IsStruct) || (typeId == TypeNodes.WithTypeId && !type.IsStruct))
        {
            return null;
        }

        (string id, string name, TypeRef propertyType) = type.Properties[index];
        return generatedDefinitions.GetOrAdd(typeId + "\n" + id + "\n" + type.Key, _ => typeId switch
        {
            TypeNodes.GetTypeId => new GetPropertyNode(type, id, name, propertyType),
            TypeNodes.SetTypeId => new SetPropertyNode(type, id, name, propertyType),
            _ => new WithPropertyNode(type, id, name, propertyType),
        });
    }

    private NodeDefinition? VariableDefinition(GraphVariable variable, bool set, IReadOnlyList<GraphDocument> projectGraphs) =>
        TypeRef.FromName(variable.Type, projectGraphs) is { } type
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
