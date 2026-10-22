using VisualStudioLikeIDE.Compiler.Ir;
using VisualStudioLikeIDE.Compiler.Lowering;

namespace VisualStudioLikeIDE.Compiler.Nodes;

/// <summary>
/// Describes a kind of node: its pins, editable properties, documentation, and how it lowers to IR.
/// Definitions are stateless and shared by every node instance of their type.
/// </summary>
public abstract class NodeDefinition
{
    private readonly Dictionary<string, PinDefinition> pinsById;

    protected NodeDefinition(
        string typeId,
        string displayName,
        string category,
        string description,
        IReadOnlyList<PinDefinition> pins,
        IReadOnlyList<PropertyDefinition>? properties = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeId);
        ArgumentNullException.ThrowIfNull(pins);
        TypeId = typeId;
        DisplayName = displayName;
        Category = category;
        Description = description;
        Pins = pins;
        Properties = properties ?? [];
        pinsById = pins.ToDictionary(pin => pin.Id, StringComparer.Ordinal);
    }

    /// <summary>Stable identifier stored in graph files. Never change it once released.</summary>
    public string TypeId { get; }

    public string DisplayName { get; }

    /// <summary>Palette category, for example <c>Math</c>. Also drives the header accent color.</summary>
    public string Category { get; }

    public string Description { get; }

    public IReadOnlyList<PinDefinition> Pins { get; }

    public IReadOnlyList<PropertyDefinition> Properties { get; }

    public PinDefinition? FindPin(string pinId) => pinsById.GetValueOrDefault(pinId);

    /// <summary>
    /// Properties a new node of this definition starts with, beyond the defaults of <see cref="Properties"/>. Variable nodes use
    /// this to record which variable they refer to; that value is not edited like a property.
    /// </summary>
    public virtual IReadOnlyDictionary<string, string> InitialProperties { get; init; } = new Dictionary<string, string>();

    public PropertyDefinition? FindProperty(string propertyId) =>
        Properties.FirstOrDefault(property => property.Id == propertyId);

    /// <summary>Returns an error message when <paramref name="value"/> is not acceptable for the property, otherwise null.</summary>
    public virtual string? ValidateProperty(PropertyDefinition definition, string value)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Choices is { } choices)
        {
            return choices.Contains(value, StringComparer.Ordinal) ? null : $"'{value}' is not one of {string.Join(", ", choices)}.";
        }

        return LiteralValues.TryParse(definition.Type, value, out _, out string? error) ? null : error;
    }
}

/// <summary>
/// A side-effect-free node with no execution pins and a single data output. Its expression is inlined where it is used.
/// </summary>
public abstract class PureNodeDefinition : NodeDefinition
{
    protected PureNodeDefinition(
        string typeId,
        string displayName,
        string category,
        string description,
        IReadOnlyList<PinDefinition> pins,
        IReadOnlyList<PropertyDefinition>? properties = null)
        : base(typeId, displayName, category, description, pins, properties)
    {
        if (pins.Any(pin => pin.IsExecution) || pins.Count(pin => pin.Direction == PinDirection.Output) != 1)
        {
            throw new ArgumentException("Pure nodes have no execution pins and exactly one output.", nameof(pins));
        }

        Output = pins.Single(pin => pin.Direction == PinDirection.Output);
    }

    public PinDefinition Output { get; }

    public abstract IrExpression Lower(ExpressionLoweringContext context);
}

/// <summary>
/// A node that runs as one or more statements in execution order.
/// </summary>
/// <remarks>
/// <see cref="Lower"/> emits the node's own statements and lowers any nested execution outputs
/// (such as branch arms) with <see cref="StatementLoweringContext.LowerBranch"/>. The compiler continues
/// with the node where the node's outgoing execution paths reconverge, so a linear node never lowers its own continuation.
/// </remarks>
public abstract class StatementNodeDefinition : NodeDefinition
{
    protected StatementNodeDefinition(
        string typeId,
        string displayName,
        string category,
        string description,
        IReadOnlyList<PinDefinition> pins,
        IReadOnlyList<PropertyDefinition>? properties = null)
        : base(typeId, displayName, category, description, pins, properties)
    {
    }

    public abstract void Lower(StatementLoweringContext context);

    /// <summary>
    /// True when the generated statement evaluates each input once, so a value used by several inputs can be computed once into a
    /// local before the statement. False for loops, whose inputs are evaluated again on every iteration.
    /// </summary>
    public virtual bool EvaluatesInputsOnce => true;
}
