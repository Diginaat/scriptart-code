using VisualStudioLikeIDE.Compiler.Ir;
using VisualStudioLikeIDE.Compiler.Lowering;
using VisualStudioLikeIDE.Compiler.TypeSystem;

namespace VisualStudioLikeIDE.Compiler.Nodes;

/// <summary>
/// Get and Set nodes for graph variables. They are not registered in the library: <see cref="NodeLibrary.Resolve"/> builds one
/// per variable, because their pin types and titles come from the variable.
/// </summary>
public static class VariableNodes
{
    public const string GetTypeId = "variable.get";
    public const string SetTypeId = "variable.set";

    /// <summary>The node property holding the id of the variable the node reads or writes.</summary>
    public const string VariableProperty = "variable";

    public const string Category = "Variables";
}

/// <summary>Reads a variable. Pure: the read happens where the value is used.</summary>
public sealed class VariableGetNode(string variableId, string variableName, TypeRef type) : PureNodeDefinition(
    VariableNodes.GetTypeId,
    $"Get {variableName}",
    VariableNodes.Category,
    $"The current value of the {type} variable '{variableName}'.",
    [PinDefinition.DataOut("value", "Value", type)])
{
    public string VariableId { get; } = variableId;

    public override IReadOnlyDictionary<string, string> InitialProperties { get; init; } =
        new Dictionary<string, string> { [VariableNodes.VariableProperty] = variableId };

    public override IrExpression Lower(ExpressionLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new IrLocalReference(context.Variable(VariableId));
    }
}

/// <summary>Assigns a new value to a variable.</summary>
public sealed class VariableSetNode(string variableId, string variableName, TypeRef type) : StatementNodeDefinition(
    VariableNodes.SetTypeId,
    $"Set {variableName}",
    VariableNodes.Category,
    $"Stores a new value in the {type} variable '{variableName}'.",
    [PinDefinition.ExecutionIn(), PinDefinition.DataIn("value", "Value", type), PinDefinition.ExecutionOut()])
{
    public string VariableId { get; } = variableId;

    public override IReadOnlyDictionary<string, string> InitialProperties { get; init; } =
        new Dictionary<string, string> { [VariableNodes.VariableProperty] = variableId };

    public override void Lower(StatementLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Emit(new IrAssignment(context.Variable(VariableId), context.Input("value")));
    }
}
