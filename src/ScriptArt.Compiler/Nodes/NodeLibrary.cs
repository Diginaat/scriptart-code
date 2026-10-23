using System.Collections.Concurrent;
using ScriptArt.Compiler.TypeSystem;
using ScriptArt.GraphModel;

namespace ScriptArt.Compiler.Nodes;

/// <summary>
/// The set of node definitions available to a project, keyed by <see cref="NodeDefinition.TypeId"/>.
/// </summary>
/// <remarks>
/// Safe to read from several threads while one thread registers: registration builds new dictionaries and publishes them whole,
/// so a reader sees the library before or after a registration, never during one.
/// </remarks>
public sealed class NodeLibrary
{
    private readonly Lock registration = new();

    private volatile int version;

    private volatile Dictionary<string, NodeDefinition> definitions = new(StringComparer.Ordinal);

    // Generic .NET methods, whose nodes are built per node from the type arguments it stores.
    private volatile Dictionary<string, ExternalMethod> genericMethods = new(StringComparer.Ordinal);

    // .NET types graphs can hold values of, by SourceName.
    private volatile Dictionary<string, ExternalType> externalTypes = new(StringComparer.Ordinal);

    // Variable nodes get their pins from the variable they refer to. Definitions are immutable, so one is shared per variable state.
    private readonly ConcurrentDictionary<(string Id, string Name, TypeRef Type, bool Set), NodeDefinition> variableDefinitions = new();

    // Function entry, return, and call nodes get their pins from a function's signature, type nodes from a class or struct, and
    // generated collection nodes from a list or dictionary type; keyed by node type and everything the pins depend on.
    private readonly ConcurrentDictionary<string, NodeDefinition> generatedDefinitions = new(StringComparer.Ordinal);

    public IEnumerable<NodeDefinition> Definitions => definitions.Values;

    /// <summary>Changes whenever nodes or types are registered, so results computed from the library can be cached by it.</summary>
    public int Version => version;

    /// <summary>The .NET types graphs can hold values of, such as <c>System.Text.StringBuilder</c>, in name order.</summary>
    public IEnumerable<ExternalType> ExternalTypes => externalTypes.Values.OrderBy(type => type.Type.SourceName, StringComparer.Ordinal);

    /// <summary>The .NET type <paramref name="type"/> refers to, or null when no referenced assembly offers it.</summary>
    public ExternalType? FindExternalType(TypeRef type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return externalTypes.GetValueOrDefault(type.SourceName);
    }

    /// <summary>
    /// The classes a value of <paramref name="type"/> derives from, nearest first: for a class of the project, its
    /// <see cref="GraphDocument.BaseType"/> and that class's bases; for a .NET class, its <see cref="ExternalType.BaseTypes"/>.
    /// A value connects to inputs of each of them.
    /// </summary>
    public IReadOnlyList<TypeRef> BaseTypesOf(TypeRef type, IEnumerable<GraphDocument> projectGraphs)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(projectGraphs);
        if (type.IsProjectType)
        {
            return projectGraphs.FirstOrDefault(graph => graph.Kind == GraphKind.Class && graph.Name == type.Name) is { BaseType: { } name }
                && TypeRef.FromName(name) is { IsExternal: true } baseType
                ? [baseType, .. FindExternalType(baseType)?.BaseTypes ?? []]
                : [];
        }

        return FindExternalType(type)?.BaseTypes ?? [];
    }

    /// <summary>
    /// The types a value of <paramref name="type"/> connects to besides its own: its base classes (<see cref="BaseTypesOf"/>) and, for
    /// any class, <see cref="TypeRef.Object"/>.
    /// </summary>
    public IReadOnlyList<TypeRef> AssignableTypesOf(TypeRef type, IReadOnlyList<GraphDocument> projectGraphs)
    {
        IReadOnlyList<TypeRef> bases = BaseTypesOf(type, projectGraphs);
        return IsClass(type, projectGraphs) ? [.. bases, TypeRef.Object] : bases;
    }

    /// <summary>
    /// The virtual method of <paramref name="owner"/>'s base classes that a method named <paramref name="name"/> with
    /// <paramref name="parameterTypes"/> overrides, nearest class first; null when there is none.
    /// </summary>
    public ExternalMethod? OverriddenMethod(GraphDocument owner, string name, IReadOnlyList<TypeRef> parameterTypes, IEnumerable<GraphDocument> projectGraphs)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(parameterTypes);
        return BaseTypesOf(TypeRef.ProjectType(owner.Name), projectGraphs)
            .SelectMany(baseType => FindExternalType(baseType)?.Overridable ?? [])
            .FirstOrDefault(method => method.Name == name && method.Parameters.Select(parameter => parameter.Type).SequenceEqual(parameterTypes));
    }

    /// <summary>
    /// The virtual methods a class graph can override: those of its base classes, nearest class first, each signature once, in name
    /// order. Empty for a class without a base type.
    /// </summary>
    public IReadOnlyList<ExternalMethod> OverridableMethods(GraphDocument owner, IEnumerable<GraphDocument> projectGraphs)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return [.. BaseTypesOf(TypeRef.ProjectType(owner.Name), projectGraphs)
            .SelectMany(baseType => FindExternalType(baseType)?.Overridable ?? [])
            .DistinctBy(method => $"{method.Name}({string.Join(",", method.Parameters.Select(parameter => parameter.Type.SourceName))})")
            .OrderBy(method => method.Name, StringComparer.Ordinal)];
    }

    public void Register(NodeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        RegisterAll([definition]);
    }

    /// <summary>Registers several definitions at once. Throws, and registers none, when a type ID is already registered.</summary>
    public void RegisterAll(IEnumerable<NodeDefinition> added)
    {
        ArgumentNullException.ThrowIfNull(added);
        lock (registration)
        {
            Dictionary<string, NodeDefinition> next = new(definitions, StringComparer.Ordinal);
            foreach (NodeDefinition definition in added)
            {
                if (!next.TryAdd(definition.TypeId, definition))
                {
                    throw new InvalidOperationException($"A node definition with type '{definition.TypeId}' is already registered.");
                }
            }

            definitions = next;
            version++;
        }
    }

    /// <summary>
    /// Registers the nodes of a node library loaded with <see cref="NodeLibraryLoader"/>, skipping (and returning a problem for)
    /// each node whose type ID is already registered.
    /// </summary>
    public IReadOnlyList<string> RegisterLibrary(NodeLibraryContents contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        List<string> problems = [];
        List<NodeDefinition> accepted = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (NodeDefinition definition in contents.Definitions)
        {
            if (Find(definition.TypeId) is not null || !seen.Add(definition.TypeId))
            {
                problems.Add($"{Path.GetFileName(contents.AssemblyPath)}: node type '{definition.TypeId}' is already defined, so this node was not added.");
            }
            else
            {
                accepted.Add(definition);
            }
        }

        RegisterAll(accepted);
        return problems;
    }

    public NodeDefinition? Find(string typeId) => definitions.GetValueOrDefault(typeId);

    /// <summary>True when <paramref name="typeId"/> is a generic .NET method, whose node needs type arguments.</summary>
    public bool IsGenericMethod(string typeId) => genericMethods.ContainsKey(typeId);

    /// <summary>
    /// The definition of a node placed in <paramref name="graph"/>. Most nodes are found by type. Variable nodes are built from the
    /// variable named by their <see cref="VariableNodes.VariableProperty"/>, function nodes from a function's signature (the graph's
    /// own for entry and return nodes, the called graph's for call nodes), type nodes from a class or struct, the generated nodes of
    /// collections from the type in <see cref="CollectionNodes.CollectionProperty"/>, and generic .NET methods from their type
    /// arguments. Nodes of a generic class or struct, and calls of its methods, take its type arguments from
    /// <see cref="TypeNodes.TypeArgumentsProperty"/>. Called functions and types are found in <paramref name="projectGraphs"/>, the
    /// project's graphs. These are null when what they refer to is missing, has an unsupported type, or does not fit the graph.
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
                    ? VariableDefinition(variable, node.Type == VariableNodes.SetTypeId, graph, projectGraphs)
                    : null;
            case FunctionNodes.EntryTypeId:
                return FunctionSignature.Of(graph, projectGraphs) is { } own ? FunctionDefinition(node.Type, own) : null;
            case FunctionNodes.ReturnTypeId:
                return FunctionSignature.Of(graph, projectGraphs) is { } returning && returning.ReturnType != TypeRef.Void ? FunctionDefinition(node.Type, returning) : null;
            case FunctionNodes.CallTypeId:
                return node.Properties.TryGetValue(FunctionNodes.FunctionProperty, out string? functionId)
                    && projectGraphs.FirstOrDefault(function => function.Id == functionId) is { } called
                    && FunctionSignature.Of(called, projectGraphs) is { IsConstructor: false } signature
                    && OwnerArguments(node, called, graph, projectGraphs) is { } callArguments
                    && signature.Instantiate(callArguments) is { } instantiated
                    ? FunctionDefinition(node.Type, instantiated)
                    : null;
            case TypeNodes.SelfTypeId:
                return FunctionSignature.Of(graph, projectGraphs) is { Owner: { } owner } ? SelfDefinition(owner) : null;
            case TypeNodes.CreateTypeId or TypeNodes.GetTypeId or TypeNodes.SetTypeId or TypeNodes.WithTypeId:
                return ResolveTypeNode(node, graph, projectGraphs);
            case string collectionNode when CollectionNodes.IsGeneratedNode(collectionNode):
                return TypeRef.FromName(node.Properties.GetValueOrDefault(CollectionNodes.CollectionProperty), projectGraphs, graph) is { } collection
                    ? CollectionDefinitions(collection).FirstOrDefault(definition => definition.TypeId == collectionNode)
                    : null;
            case SignalNodes.EmitTypeId or SignalNodes.OnTypeId:
                return projectGraphs.FirstOrDefault(type => type.Id == node.Properties.GetValueOrDefault(TypeNodes.TypeProperty)) is { } signalType
                    && SignalNodes.Of(signalType, node.Properties.GetValueOrDefault(SignalNodes.SignalProperty), projectGraphs) is { } signal
                    ? node.Type == SignalNodes.EmitTypeId ? new EmitSignalNode(signal) : new OnSignalNode(signal)
                    : null;
            case TypeNodes.CastTypeId:
                return TypeRef.FromName(node.Properties.GetValueOrDefault(TypeNodes.CastProperty), projectGraphs, graph) is { } castType
                    && castType != TypeRef.String && !castType.IsList && !castType.IsDictionary && IsClass(castType, projectGraphs)
                    ? generatedDefinitions.GetOrAdd(TypeNodes.CastTypeId + "\n" + castType.SourceName, _ => new CastNode(castType))
                    : null;
            case string generic when genericMethods.TryGetValue(generic, out ExternalMethod? method):
                return TypeRef.ArgumentsFromNames(node.Properties.GetValueOrDefault(ExternalApi.TypeArgumentsProperty), projectGraphs, graph) is { } typeArguments
                    && method.Instantiate(typeArguments) is { } instance
                    && method.TypeParameters.Zip(typeArguments).All(pair => !method.ClassTypeParameters.Contains(pair.First) || IsClass(pair.Second, projectGraphs))
                    ? generatedDefinitions.GetOrAdd(generic + "\n" + TypeRef.ArgumentNames(typeArguments), _ => ExternalApi.CreateNode(instance))
                    : null;
            default:
                return Find(node.Type);
        }
    }

    // True when type satisfies a class constraint. A type parameter of the project's generic types is unconstrained, so it does not.
    private bool IsClass(TypeRef type, IReadOnlyList<GraphDocument> projectGraphs) =>
        type == TypeRef.String
        || type.IsList || type.IsDictionary
        || (type.IsProjectType && projectGraphs.Any(graph => graph.Kind == GraphKind.Class && graph.Name == type.Name))
        || (type.IsExternal && FindExternalType(type) is { IsValueType: false });

    private NodeDefinition? ResolveTypeNode(GraphNode node, GraphDocument graph, IReadOnlyList<GraphDocument> projectGraphs)
    {
        if (!node.Properties.TryGetValue(TypeNodes.TypeProperty, out string? typeId)
            || projectGraphs.FirstOrDefault(type => type.Id == typeId && TypeRef.IsTypeGraph(type)) is not { } typeGraph
            || OwnerArguments(node, typeGraph, graph, projectGraphs) is not { } typeArguments
            || ProjectTypeDefinition.Of(typeGraph, projectGraphs, typeArguments) is not { } definition)
        {
            return null;
        }

        if (node.Type != TypeNodes.CreateTypeId)
        {
            return TypeDefinition(node.Type, definition, node.Properties.GetValueOrDefault(TypeNodes.PropertyProperty), IsConstructorOf(graph, typeGraph));
        }

        if (!node.Properties.TryGetValue(TypeNodes.ConstructorProperty, out string? constructorId))
        {
            return CreateDefinition(definition, null);
        }

        return projectGraphs.FirstOrDefault(constructor => constructor.Id == constructorId && constructor.Kind == GraphKind.Constructor && constructor.Owner == typeGraph.Id) is { } constructorGraph
            && FunctionSignature.Of(constructorGraph, projectGraphs)?.Instantiate(typeArguments) is { } signature
            ? CreateDefinition(definition, signature)
            : null;
    }

    /// <summary>
    /// The type arguments a node gives the generic class or struct <paramref name="owner"/>, or of the owner of a method it calls:
    /// none for a type that is not generic, null when they are missing or do not fit.
    /// </summary>
    private static IReadOnlyList<TypeRef>? OwnerArguments(GraphNode node, GraphDocument ownerOrMethod, GraphDocument graph, IReadOnlyList<GraphDocument> projectGraphs)
    {
        GraphDocument? owner = TypeRef.IsTypeGraph(ownerOrMethod) ? ownerOrMethod : projectGraphs.FirstOrDefault(type => type.Id == ownerOrMethod.Owner);
        if (owner is null || owner.TypeParameters.Count == 0)
        {
            return [];
        }

        return TypeRef.ArgumentsFromNames(node.Properties.GetValueOrDefault(TypeNodes.TypeArgumentsProperty), projectGraphs, graph) is { } arguments
            && arguments.Count == owner.TypeParameters.Count
            ? arguments
            : null;
    }

    private static bool IsConstructorOf(GraphDocument graph, GraphDocument type) => graph.Kind == GraphKind.Constructor && graph.Owner == type.Id;

    /// <summary>
    /// Every node that can be added to <paramref name="graph"/>: the registered nodes, a Get and a Set node per variable, the entry
    /// and return nodes of a function graph (and Self in a method or constructor of a class or struct), a call node per function or
    /// method in <paramref name="projectGraphs"/>, per class or struct a New node per constructor and nodes for its properties, and
    /// the nodes of every collection that a variable, parameter, return value, or property of the project uses. A generic class or
    /// struct offers its nodes for each type argument list the project uses, and for its own type parameters inside its methods. A
    /// class or struct graph has no method, so nothing can be added to it.
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
            new[] { VariableDefinition(variable, set: false, graph, projectGraphs), VariableDefinition(variable, set: true, graph, projectGraphs) });
        if (FunctionSignature.Of(graph, projectGraphs) is { } own)
        {
            graphNodes = graphNodes.Append(FunctionDefinition(FunctionNodes.EntryTypeId, own));
            if (own.ReturnType != TypeRef.Void)
            {
                graphNodes = graphNodes.Append(FunctionDefinition(FunctionNodes.ReturnTypeId, own));
            }

            if (own.Owner is { } owner)
            {
                graphNodes = graphNodes.Append(SelfDefinition(owner));
            }
        }

        TypeRef[] used = [.. UsedTypes(graph, projectGraphs)];

        // The type arguments each class or struct is used with: none for one that is not generic.
        IEnumerable<(GraphDocument Type, IReadOnlyList<TypeRef> Arguments)> instances = projectGraphs.Where(TypeRef.IsTypeGraph).SelectMany(type => type.TypeParameters.Count == 0
            ? [(type, (IReadOnlyList<TypeRef>)[])]
            : used.Where(candidate => candidate.IsProjectType && candidate.Name == type.Name && candidate.Arguments.Count == type.TypeParameters.Count)
                .Select(candidate => (type, candidate.Arguments)));

        List<NodeDefinition?> typeNodes = [];
        foreach ((GraphDocument type, IReadOnlyList<TypeRef> arguments) in instances)
        {
            if (ProjectTypeDefinition.Of(type, projectGraphs, arguments) is not { } definition)
            {
                continue;
            }

            typeNodes.Add(CreateDefinition(definition, null));
            typeNodes.AddRange(projectGraphs
                .Where(constructor => constructor.Kind == GraphKind.Constructor && constructor.Owner == type.Id)
                .Select(constructor => FunctionSignature.Of(constructor, projectGraphs)?.Instantiate(arguments))
                .OfType<FunctionSignature>()
                .Select(signature => CreateDefinition(definition, signature)));
            foreach ((string propertyId, _, _) in definition.Properties)
            {
                typeNodes.Add(TypeDefinition(TypeNodes.GetTypeId, definition, propertyId, inConstructor: false));
                typeNodes.Add(TypeDefinition(definition.IsStruct ? TypeNodes.WithTypeId : TypeNodes.SetTypeId, definition, propertyId, inConstructor: false));
                if (definition.IsStruct && IsConstructorOf(graph, type))
                {
                    typeNodes.Add(TypeDefinition(TypeNodes.SetTypeId, definition, propertyId, inConstructor: true));
                }
            }

            typeNodes.AddRange(projectGraphs
                .Where(method => method.Kind == GraphKind.Function && method.Owner == type.Id)
                .Select(method => FunctionSignature.Of(method, projectGraphs)?.Instantiate(arguments))
                .OfType<FunctionSignature>()
                .Select(signature => FunctionDefinition(FunctionNodes.CallTypeId, signature)));
        }

        IEnumerable<NodeDefinition?> functions = projectGraphs
            .Where(function => function.Kind == GraphKind.Function && function.Owner is null)
            .Select(function => FunctionSignature.Of(function, projectGraphs))
            .OfType<FunctionSignature>()
            .Select(signature => FunctionDefinition(FunctionNodes.CallTypeId, signature));
        IEnumerable<NodeDefinition> generic = genericMethods.Values.Select(method => generatedDefinitions.GetOrAdd(
            method.TypeId + "\n" + ExternalApi.DefaultTypeArguments(method),
            _ => ExternalApi.CreateNode(method.Instantiate([.. method.TypeParameters.Select(_ => TypeRef.Int32)])!)));
        // Cast To for each class of the project and each .NET class: the classes a value may turn out to be.
        IEnumerable<NodeDefinition> casts = projectGraphs.Where(type => type.Kind == GraphKind.Class && type.TypeParameters.Count == 0)
            .Select(type => TypeRef.ProjectType(type.Name))
            .Concat(ExternalTypes.Where(type => !type.IsValueType).Select(type => type.Type))
            .Select(type => generatedDefinitions.GetOrAdd(TypeNodes.CastTypeId + "\n" + type.SourceName, _ => new CastNode(type)));
        return Definitions
            .Concat(graphNodes.Concat(functions).Concat(typeNodes).OfType<NodeDefinition>())
            .Concat(generic)
            .Concat(casts)
            .Concat(projectGraphs.Where(type => type.Kind == GraphKind.Class).SelectMany(type => SignalNodes.For(type, projectGraphs)))
            .Concat(used.SelectMany(CollectionDefinitions));
    }

    /// <summary>
    /// Every type, and type argument within one, that a variable, parameter, return value, or property of the project uses, each read
    /// in its own graph; and inside a method or constructor of a generic type, that type with its own type parameters.
    /// </summary>
    private static IEnumerable<TypeRef> UsedTypes(GraphDocument graph, IReadOnlyList<GraphDocument> projectGraphs)
    {
        IEnumerable<TypeRef> declared = projectGraphs.Append(graph).Distinct()
            .SelectMany(used => used.Variables.Select(variable => variable.Type)
                .Concat(used.Parameters.Select(parameter => parameter.Type))
                .Concat(used.Properties.Select(property => property.Type))
                .Append(used.ReturnType)
                .Select(name => TypeRef.FromName(name, projectGraphs, used)))
            .OfType<TypeRef>();
        if (FunctionSignature.Of(graph, projectGraphs)?.Owner is { } owner)
        {
            declared = declared.Append(owner);
        }

        return declared.SelectMany(type => type.SelfAndArguments()).Distinct();
    }

    private IEnumerable<NodeDefinition> CollectionDefinitions(TypeRef collection) =>
        CollectionNodes.HasGeneratedNodes(collection)
            ? CollectionNodes.For(collection).Select(definition => generatedDefinitions.GetOrAdd(definition.TypeId + "\n" + collection.SourceName, _ => definition))
            : [];

    private NodeDefinition SelfDefinition(TypeRef owner) =>
        generatedDefinitions.GetOrAdd(TypeNodes.SelfTypeId + "\n" + owner.SourceName, _ => new SelfNode(owner));

    private NodeDefinition FunctionDefinition(string typeId, FunctionSignature signature) =>
        generatedDefinitions.GetOrAdd(typeId + "\n" + signature.Key, _ => typeId switch
        {
            FunctionNodes.EntryTypeId => new FunctionEntryNode(signature),
            FunctionNodes.ReturnTypeId => new FunctionReturnNode(signature.ReturnType),
            _ => new FunctionCallNode(signature),
        });

    /// <summary>The New node of <paramref name="type"/>, running <paramref name="constructor"/>, or <c>new T()</c> when it is null.</summary>
    private NodeDefinition CreateDefinition(ProjectTypeDefinition type, FunctionSignature? constructor) =>
        generatedDefinitions.GetOrAdd(TypeNodes.CreateTypeId + "\n" + type.Key + "\n" + constructor?.Key, _ => (type.IsStruct, constructor) switch
        {
            (true, _) => new CreateStructNode(type, constructor),
            _ => new CreateObjectNode(type, constructor),
        });

    /// <summary>
    /// A property node of <paramref name="type"/>. Null when the property is missing, for With on a class, and for Set on a struct
    /// outside its constructors: a struct property is changed by copying the value, a class property by assigning it. In a
    /// constructor of a struct, Set assigns a property of the value being created.
    /// </summary>
    private NodeDefinition? TypeDefinition(string typeId, ProjectTypeDefinition type, string? propertyId, bool inConstructor)
    {
        int index = type.Properties.Select(candidate => candidate.Id).ToList().IndexOf(propertyId ?? "");
        bool onSelf = typeId == TypeNodes.SetTypeId && type.IsStruct;
        if (index < 0 || (onSelf && !inConstructor) || (typeId == TypeNodes.WithTypeId && !type.IsStruct))
        {
            return null;
        }

        (string id, string name, TypeRef propertyType) = type.Properties[index];
        return generatedDefinitions.GetOrAdd(typeId + "\n" + id + "\n" + type.Key, _ => typeId switch
        {
            TypeNodes.GetTypeId => new GetPropertyNode(type, id, name, propertyType),
            TypeNodes.SetTypeId => new SetPropertyNode(type, id, name, propertyType, onSelf),
            _ => new WithPropertyNode(type, id, name, propertyType),
        });
    }

    private NodeDefinition? VariableDefinition(GraphVariable variable, bool set, GraphDocument graph, IReadOnlyList<GraphDocument> projectGraphs) =>
        TypeRef.FromName(variable.Type, projectGraphs, graph) is { } type
            ? variableDefinitions.GetOrAdd((variable.Id, variable.Name, type, set), key => key.Set
                ? new VariableSetNode(key.Id, key.Name, key.Type)
                : new VariableGetNode(key.Id, key.Name, key.Type))
            : null;

    /// <summary>
    /// Creates a library containing every built-in node, a node per usable member of <see cref="ExternalApi.FrameworkTypes"/> and
    /// <see cref="ExternalApi.FrameworkValueTypes"/>, and a node per usable member of the assemblies at <paramref name="referencePaths"/>,
    /// such as a project's packages.
    /// </summary>
    public static NodeLibrary CreateDefault(IReadOnlyList<string>? referencePaths = null)
    {
        var library = new NodeLibrary();
        foreach (NodeDefinition definition in BuiltInNodes.All())
        {
            library.Register(definition);
        }

        library.RegisterExternal(new ExternalApiContents(ExternalApi.Framework, ExternalApi.FrameworkTypesOfValues));
        library.RegisterExternal(ExternalApi.FromAssemblies(referencePaths ?? []));
        return library;
    }

    /// <summary>
    /// Adds a node per member and the types graphs can hold values of, for example after a restore made a package's assemblies known.
    /// Members and types already known are skipped, so a package that forwards a type to the framework adds nothing twice.
    /// </summary>
    public void RegisterExternal(ExternalApiContents contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        lock (registration)
        {
            Dictionary<string, ExternalType> types = new(externalTypes, StringComparer.Ordinal);
            foreach (ExternalType type in contents.Types)
            {
                types.TryAdd(type.Type.SourceName, type);
            }

            Dictionary<string, ExternalMethod> generic = new(genericMethods, StringComparer.Ordinal);
            Dictionary<string, NodeDefinition> nodes = new(definitions, StringComparer.Ordinal);
            foreach (ExternalMethod method in contents.Members)
            {
                if (method.IsGeneric)
                {
                    generic.TryAdd(method.TypeId, method);
                }
                else if (!nodes.ContainsKey(method.TypeId))
                {
                    nodes.Add(method.TypeId, ExternalApi.CreateNode(method));
                }
            }

            externalTypes = types;
            genericMethods = generic;
            definitions = nodes;
            version++;
        }
    }
}
