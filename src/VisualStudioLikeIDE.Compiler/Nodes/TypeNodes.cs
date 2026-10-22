using System.Globalization;
using VisualStudioLikeIDE.Compiler.Ir;
using VisualStudioLikeIDE.Compiler.Lowering;
using VisualStudioLikeIDE.Compiler.TypeSystem;
using VisualStudioLikeIDE.GraphModel;

namespace VisualStudioLikeIDE.Compiler.Nodes;

/// <summary>
/// Nodes for the classes and structs of a project. Like function nodes they are not registered: <see cref="NodeLibrary.Resolve"/>
/// builds them from a type graph, so their pins follow the type's name and properties.
/// </summary>
public static class TypeNodes
{
    /// <summary>Creates a new object of the type named by <see cref="TypeProperty"/>.</summary>
    public const string CreateTypeId = "type.create";

    /// <summary>Reads the property named by <see cref="PropertyProperty"/>.</summary>
    public const string GetTypeId = "type.get-property";

    /// <summary>Assigns a property of a class object.</summary>
    public const string SetTypeId = "type.set-property";

    /// <summary>Copies a struct value with one property changed.</summary>
    public const string WithTypeId = "type.with-property";

    /// <summary>The node property holding the id of the type graph.</summary>
    public const string TypeProperty = "type";

    /// <summary>The node property holding the id of the type's property that the node reads or writes.</summary>
    public const string PropertyProperty = "property";

    public const string Category = "Types";

    /// <summary>True for the node types whose pins come from a class or struct.</summary>
    public static bool IsTypeNode(string typeId) => typeId is CreateTypeId or GetTypeId or SetTypeId or WithTypeId;

    /// <summary>The local name a value of <paramref name="type"/> is stored in: Point → point.</summary>
    internal static string NameHint(string type) => char.ToLower(type[0], CultureInfo.InvariantCulture) + type[1..];
}

/// <summary>A class or struct of the project, with every property type resolved. Property ids double as node references.</summary>
public sealed record ProjectTypeDefinition(string GraphId, TypeRef Type, bool IsStruct, IReadOnlyList<(string Id, string Name, TypeRef Type)> Properties)
{
    /// <summary>
    /// The definition of <paramref name="graph"/>, whose property types are resolved against <paramref name="projectGraphs"/>; null
    /// when it is not a class or struct or a property has an unsupported type.
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
            if (TypeRef.FromName(property.Type, projectGraphs) is not { } type)
            {
                return null;
            }

            properties.Add((property.Id, property.Name, type));
        }

        return new ProjectTypeDefinition(graph.Id, TypeRef.ProjectType(graph.Name), graph.Kind == GraphKind.Struct, properties);
    }

    public string Name => Type.Name;

    /// <summary>A text form of everything the generated nodes depend on, used as a cache key.</summary>
    internal string Key => string.Join("\n", [GraphId, Name, IsStruct.ToString(), .. Properties.Select(property => $"{property.Id}:{property.Name}:{property.Type}")]);
}

/// <summary>
/// Creates a class object. A statement node, because each run of it makes a distinct object: inlined at every use, two uses would
/// see two different objects.
/// </summary>
public sealed class CreateObjectNode(ProjectTypeDefinition type) : StatementNodeDefinition(
    TypeNodes.CreateTypeId,
    $"New {type.Name}",
    TypeNodes.Category,
    $"Creates a new {type.Name} object whose properties have their initial values.",
    [PinDefinition.ExecutionIn(), PinDefinition.ExecutionOut(), PinDefinition.DataOut("value", type.Name, type.Type)])
{
    public ProjectTypeDefinition Type { get; } = type;

    public override IReadOnlyDictionary<string, string> InitialProperties { get; } =
        new Dictionary<string, string> { [TypeNodes.TypeProperty] = type.GraphId };

    public override void Lower(StatementLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var creation = new IrObjectCreation(Type.Type, []);
        context.DeclareOutput("value", creation, TypeNodes.NameHint(Type.Name), whenUnused: creation);
    }
}

/// <summary>A new struct value. Pure: a struct has no identity, so creating it where it is used is the same as creating it once.</summary>
public sealed class CreateStructNode(ProjectTypeDefinition type) : PureNodeDefinition(
    TypeNodes.CreateTypeId,
    $"New {type.Name}",
    TypeNodes.Category,
    $"A new {type.Name} value whose properties have their initial values.",
    [PinDefinition.DataOut("value", type.Name, type.Type)])
{
    public ProjectTypeDefinition Type { get; } = type;

    public override IReadOnlyDictionary<string, string> InitialProperties { get; } =
        new Dictionary<string, string> { [TypeNodes.TypeProperty] = type.GraphId };

    public override IrExpression Lower(ExpressionLoweringContext context) => new IrObjectCreation(Type.Type, []);
}

/// <summary>Reads a property of a class object or struct value.</summary>
public sealed class GetPropertyNode(ProjectTypeDefinition type, string propertyId, string propertyName, TypeRef propertyType) : PureNodeDefinition(
    TypeNodes.GetTypeId,
    $"Get {type.Name}.{propertyName}",
    TypeNodes.Category,
    $"The {propertyType} property {propertyName} of a {type.Name}.",
    [PinDefinition.DataIn("object", type.Name, type.Type), PinDefinition.DataOut("value", propertyName, propertyType)])
{
    public string PropertyName { get; } = propertyName;

    public TypeRef PropertyType { get; } = propertyType;

    public override IReadOnlyDictionary<string, string> InitialProperties { get; } =
        new Dictionary<string, string> { [TypeNodes.TypeProperty] = type.GraphId, [TypeNodes.PropertyProperty] = propertyId };

    public override IrExpression Lower(ExpressionLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new IrMemberAccess(context.Input("object"), PropertyName, PropertyType);
    }
}

/// <summary>Assigns a property of a class object. Everything that refers to the object sees the new value.</summary>
public sealed class SetPropertyNode(ProjectTypeDefinition type, string propertyId, string propertyName, TypeRef propertyType) : StatementNodeDefinition(
    TypeNodes.SetTypeId,
    $"Set {type.Name}.{propertyName}",
    TypeNodes.Category,
    $"Stores a new value in the {propertyType} property {propertyName} of a {type.Name} object.",
    [PinDefinition.ExecutionIn(), PinDefinition.DataIn("object", type.Name, type.Type), PinDefinition.DataIn("value", propertyName, propertyType), PinDefinition.ExecutionOut()])
{
    public string PropertyName { get; } = propertyName;

    public TypeRef PropertyType { get; } = propertyType;

    public override IReadOnlyDictionary<string, string> InitialProperties { get; } =
        new Dictionary<string, string> { [TypeNodes.TypeProperty] = type.GraphId, [TypeNodes.PropertyProperty] = propertyId };

    public override void Lower(StatementLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Emit(new IrMemberAssignment(context.Input("object"), PropertyName, context.Input("value")));
    }
}

/// <summary>
/// A copy of a struct value with one property changed. Structs are values, so changing one means storing the copy, for example
/// with a Set node of the variable it came from.
/// </summary>
public sealed class WithPropertyNode(ProjectTypeDefinition type, string propertyId, string propertyName, TypeRef propertyType) : PureNodeDefinition(
    TypeNodes.WithTypeId,
    $"{type.Name} With {propertyName}",
    TypeNodes.Category,
    $"A copy of a {type.Name} value whose {propertyType} property {propertyName} is replaced.",
    [PinDefinition.DataIn("object", type.Name, type.Type), PinDefinition.DataIn("value", propertyName, propertyType), PinDefinition.DataOut("result", type.Name, type.Type)])
{
    public string PropertyName { get; } = propertyName;

    public TypeRef PropertyType { get; } = propertyType;

    public override IReadOnlyDictionary<string, string> InitialProperties { get; } =
        new Dictionary<string, string> { [TypeNodes.TypeProperty] = type.GraphId, [TypeNodes.PropertyProperty] = propertyId };

    public override IrExpression Lower(ExpressionLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new IrWith(context.Input("object"), PropertyName, context.Input("value"));
    }
}
