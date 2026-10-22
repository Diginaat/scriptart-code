using System.Globalization;
using ScriptArt.Compiler.Ir;
using ScriptArt.Compiler.Lowering;
using ScriptArt.Compiler.TypeSystem;
using ScriptArt.GraphModel;

namespace ScriptArt.Compiler.Nodes;

/// <summary>
/// Nodes for the classes and structs of a project. Like function nodes they are not registered: <see cref="NodeLibrary.Resolve"/>
/// builds them from a type graph, so their pins follow the type's name and properties.
/// </summary>
public static class TypeNodes
{
    /// <summary>Creates a new object of the type named by <see cref="TypeProperty"/>, with the constructor named by <see cref="ConstructorProperty"/> if any.</summary>
    public const string CreateTypeId = "type.create";

    /// <summary>Reads the property named by <see cref="PropertyProperty"/>.</summary>
    public const string GetTypeId = "type.get-property";

    /// <summary>Assigns a property of a class object, or of the struct value a constructor is creating.</summary>
    public const string SetTypeId = "type.set-property";

    /// <summary>Copies a struct value with one property changed.</summary>
    public const string WithTypeId = "type.with-property";

    /// <summary>The object or value whose method is running, in a method graph of a class or struct.</summary>
    public const string SelfTypeId = "type.self";

    /// <summary>The node property holding the id of the type graph.</summary>
    public const string TypeProperty = "type";

    /// <summary>The node property holding the id of the type's property that the node reads or writes.</summary>
    public const string PropertyProperty = "property";

    /// <summary>The New node property holding the id of the constructor graph it runs; absent for <c>new T()</c>.</summary>
    public const string ConstructorProperty = "constructor";

    /// <summary>
    /// For a generic class or struct, the node property holding the type arguments the node works with, such as <c>int</c> for
    /// <c>Box&lt;int&gt;</c>. Type and call nodes store it; it is read like <see cref="TypeRef.ArgumentsFromNames"/>.
    /// </summary>
    public const string TypeArgumentsProperty = "typeArguments";

    public const string Category = "Types";

    /// <summary>True for the node types whose pins come from a class or struct.</summary>
    public static bool IsTypeNode(string typeId) => typeId is CreateTypeId or GetTypeId or SetTypeId or WithTypeId or SelfTypeId;

    /// <summary>The local name a value of <paramref name="type"/> is stored in: Point → point.</summary>
    internal static string NameHint(string type) => char.ToLower(type[0], CultureInfo.InvariantCulture) + type[1..];
}

/// <summary>
/// A class or struct of the project, with every property type resolved. Property ids double as node references. For a generic type,
/// <see cref="Type"/> carries the type arguments the definition was made for, and property types have them substituted.
/// </summary>
public sealed record ProjectTypeDefinition(string GraphId, TypeRef Type, bool IsStruct, IReadOnlyList<(string Id, string Name, TypeRef Type)> Properties)
{
    /// <summary>
    /// The definition of <paramref name="graph"/>, whose property types are resolved against <paramref name="projectGraphs"/>; null
    /// when it is not a class or struct or a property has an unsupported type. A generic type keeps its type parameters, as in
    /// <c>Box&lt;T&gt;</c>.
    /// </summary>
    public static ProjectTypeDefinition? Of(GraphDocument graph, IReadOnlyList<GraphDocument> projectGraphs)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (!TypeRef.IsTypeGraph(graph))
        {
            return null;
        }

        List<(string, string, TypeRef)> properties = [];
        foreach (GraphProperty property in graph.Properties)
        {
            if (TypeRef.FromName(property.Type, projectGraphs, graph) is not { } type)
            {
                return null;
            }

            properties.Add((property.Id, property.Name, type));
        }

        TypeRef self = TypeRef.ProjectType(graph.Name, [.. graph.TypeParameters.Select(TypeRef.TypeParameter)]);
        return new ProjectTypeDefinition(graph.Id, self, graph.Kind == GraphKind.Struct, properties);
    }

    /// <summary>
    /// The definition of <paramref name="graph"/> with <paramref name="typeArguments"/> for its type parameters, as in
    /// <c>Box&lt;int&gt;</c>; null when the number of arguments does not match.
    /// </summary>
    public static ProjectTypeDefinition? Of(GraphDocument graph, IReadOnlyList<GraphDocument> projectGraphs, IReadOnlyList<TypeRef> typeArguments)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(typeArguments);
        if (Of(graph, projectGraphs) is not { } open || graph.TypeParameters.Count != typeArguments.Count)
        {
            return null;
        }

        Dictionary<string, TypeRef> map = TypeArgumentMap(graph.TypeParameters, typeArguments);
        return open with
        {
            Type = open.Type.Substitute(map),
            Properties = [.. open.Properties.Select(property => (property.Id, property.Name, property.Type.Substitute(map)))],
        };
    }

    internal static Dictionary<string, TypeRef> TypeArgumentMap(IReadOnlyList<string> typeParameters, IReadOnlyList<TypeRef> typeArguments) =>
        typeParameters.Zip(typeArguments).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);

    public string Name => Type.Name;

    /// <summary>The node properties naming this type: its graph and, for a generic type, its type arguments.</summary>
    internal Dictionary<string, string> NodeProperties => Type.Arguments.Count == 0
        ? new() { [TypeNodes.TypeProperty] = GraphId }
        : new() { [TypeNodes.TypeProperty] = GraphId, [TypeNodes.TypeArgumentsProperty] = TypeRef.ArgumentNames(Type.Arguments) };

    /// <summary>A text form of everything the generated nodes depend on, used as a cache key.</summary>
    internal string Key => string.Join("\n", [GraphId, Type.SourceName, IsStruct.ToString(), .. Properties.Select(property => $"{property.Id}:{property.Name}:{property.Type.SourceName}")]);
}

/// <summary>
/// Creates a class object, with <c>new T()</c> or one of the type's constructors. A statement node, because each run of it makes a
/// distinct object: inlined at every use, two uses would see two different objects.
/// </summary>
public sealed class CreateObjectNode(ProjectTypeDefinition type, FunctionSignature? constructor = null) : StatementNodeDefinition(
    TypeNodes.CreateTypeId,
    CreateNodes.Title(type, constructor),
    TypeNodes.Category,
    CreateNodes.Description(type, constructor, "object"),
    [PinDefinition.ExecutionIn(), .. CreateNodes.Inputs(constructor), PinDefinition.ExecutionOut(), PinDefinition.DataOut("value", type.Name, type.Type)])
{
    public ProjectTypeDefinition Type { get; } = type;

    /// <summary>The constructor the node runs; null for <c>new T()</c>.</summary>
    public FunctionSignature? Constructor { get; } = constructor;

    public override IReadOnlyDictionary<string, string> InitialProperties { get; init; } = CreateNodes.Properties(type, constructor);

    public override void Lower(StatementLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IrExpression creation = CreateNodes.Lower(Type, Constructor, context);
        context.DeclareOutput("value", creation, TypeNodes.NameHint(Type.Name), whenUnused: creation);
    }
}

/// <summary>A new struct value. Pure: a struct has no identity, so creating it where it is used is the same as creating it once.</summary>
public sealed class CreateStructNode(ProjectTypeDefinition type, FunctionSignature? constructor = null) : PureNodeDefinition(
    TypeNodes.CreateTypeId,
    CreateNodes.Title(type, constructor),
    TypeNodes.Category,
    CreateNodes.Description(type, constructor, "value"),
    [.. CreateNodes.Inputs(constructor), PinDefinition.DataOut("value", type.Name, type.Type)])
{
    public ProjectTypeDefinition Type { get; } = type;

    /// <summary>The constructor the node runs; null for <c>new T()</c>.</summary>
    public FunctionSignature? Constructor { get; } = constructor;

    public override IReadOnlyDictionary<string, string> InitialProperties { get; init; } = CreateNodes.Properties(type, constructor);

    public override IrExpression Lower(ExpressionLoweringContext context) => CreateNodes.Lower(Type, Constructor, context);
}

/// <summary>What the New nodes of classes and structs share. A constructor's parameters are its inputs, with the parameter ids as pin ids.</summary>
internal static class CreateNodes
{
    public static string Title(ProjectTypeDefinition type, FunctionSignature? constructor) =>
        constructor is null ? $"New {type.Type}" : $"New {type.Type}({string.Join(", ", constructor.Parameters.Select(parameter => parameter.Name))})";

    public static string Description(ProjectTypeDefinition type, FunctionSignature? constructor, string kind) => constructor is null
        ? $"A new {type.Type} {kind} whose properties have their initial values."
        : $"A new {type.Type} {kind}, set up by its constructor {constructor.Name}.";

    public static IEnumerable<PinDefinition> Inputs(FunctionSignature? constructor) =>
        constructor?.Parameters.Select(parameter => PinDefinition.DataIn(parameter.Id, parameter.Name, parameter.Type)) ?? [];

    public static Dictionary<string, string> Properties(ProjectTypeDefinition type, FunctionSignature? constructor)
    {
        Dictionary<string, string> properties = type.NodeProperties;
        if (constructor is not null)
        {
            properties[TypeNodes.ConstructorProperty] = constructor.GraphId;
        }

        return properties;
    }

    public static IrExpression Lower(ProjectTypeDefinition type, FunctionSignature? constructor, ExpressionLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new IrObjectCreation(type.Type, [.. constructor?.Parameters.Select(parameter => context.Input(parameter.Id)) ?? []]);
    }
}

/// <summary>Reads a property of a class object or struct value.</summary>
public sealed class GetPropertyNode(ProjectTypeDefinition type, string propertyId, string propertyName, TypeRef propertyType) : PureNodeDefinition(
    TypeNodes.GetTypeId,
    $"Get {type.Type}.{propertyName}",
    TypeNodes.Category,
    $"The {propertyType} property {propertyName} of a {type.Type}.",
    [PinDefinition.DataIn("object", type.Name, type.Type), PinDefinition.DataOut("value", propertyName, propertyType)])
{
    public string PropertyName { get; } = propertyName;

    public TypeRef PropertyType { get; } = propertyType;

    public override IReadOnlyDictionary<string, string> InitialProperties { get; init; } =
        new Dictionary<string, string>(type.NodeProperties) { [TypeNodes.PropertyProperty] = propertyId };

    public override IrExpression Lower(ExpressionLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new IrMemberAccess(context.Input("object"), PropertyName, PropertyType);
    }
}

/// <summary>
/// Assigns a property of a class object. Everything that refers to the object sees the new value. In a constructor of a struct,
/// <paramref name="onSelf"/> assigns a property of the value being created instead, and the node has no object input.
/// </summary>
public sealed class SetPropertyNode(ProjectTypeDefinition type, string propertyId, string propertyName, TypeRef propertyType, bool onSelf = false) : StatementNodeDefinition(
    TypeNodes.SetTypeId,
    onSelf ? $"Set Self.{propertyName}" : $"Set {type.Type}.{propertyName}",
    TypeNodes.Category,
    onSelf
        ? $"Stores a new value in the {propertyType} property {propertyName} of the {type.Type} this constructor creates."
        : $"Stores a new value in the {propertyType} property {propertyName} of a {type.Type} object.",
    [
        PinDefinition.ExecutionIn(),
        .. onSelf ? [] : new[] { PinDefinition.DataIn("object", type.Name, type.Type) },
        PinDefinition.DataIn("value", propertyName, propertyType),
        PinDefinition.ExecutionOut(),
    ])
{
    public string PropertyName { get; } = propertyName;

    public TypeRef PropertyType { get; } = propertyType;

    /// <summary>True for Set Self in a struct's constructor, which has no object input.</summary>
    public bool OnSelf { get; } = onSelf;

    private TypeRef Owner { get; } = type.Type;

    public override IReadOnlyDictionary<string, string> InitialProperties { get; init; } =
        new Dictionary<string, string>(type.NodeProperties) { [TypeNodes.PropertyProperty] = propertyId };

    public override void Lower(StatementLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IrExpression target = OnSelf ? new IrThis(Owner) : context.Input("object");
        context.Emit(new IrMemberAssignment(target, PropertyName, context.Input("value")));
    }
}

/// <summary>
/// A copy of a struct value with one property changed. Structs are values, so changing one means storing the copy, for example
/// with a Set node of the variable it came from.
/// </summary>
public sealed class WithPropertyNode(ProjectTypeDefinition type, string propertyId, string propertyName, TypeRef propertyType) : PureNodeDefinition(
    TypeNodes.WithTypeId,
    $"{type.Type} With {propertyName}",
    TypeNodes.Category,
    $"A copy of a {type.Type} value whose {propertyType} property {propertyName} is replaced.",
    [PinDefinition.DataIn("object", type.Name, type.Type), PinDefinition.DataIn("value", propertyName, propertyType), PinDefinition.DataOut("result", type.Name, type.Type)])
{
    public string PropertyName { get; } = propertyName;

    public TypeRef PropertyType { get; } = propertyType;

    public override IReadOnlyDictionary<string, string> InitialProperties { get; init; } =
        new Dictionary<string, string>(type.NodeProperties) { [TypeNodes.PropertyProperty] = propertyId };

    public override IrExpression Lower(ExpressionLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new IrWith(context.Input("object"), PropertyName, context.Input("value"));
    }
}

/// <summary>
/// The object or value whose method is running, available in the method and constructor graphs of a class or struct. Pure, and
/// found from the graph's owner rather than a node property. Generates <c>this</c>.
/// </summary>
public sealed class SelfNode(TypeRef owner) : PureNodeDefinition(
    TypeNodes.SelfTypeId,
    "Self",
    TypeNodes.Category,
    $"The {owner} whose method is running. Generates this.",
    [PinDefinition.DataOut("self", "Self", owner)])
{
    public TypeRef Owner { get; } = owner;

    public override IrExpression Lower(ExpressionLoweringContext context) => new IrThis(Owner);
}
