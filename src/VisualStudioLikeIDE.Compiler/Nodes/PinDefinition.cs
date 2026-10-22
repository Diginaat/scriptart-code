using VisualStudioLikeIDE.Compiler.TypeSystem;

namespace VisualStudioLikeIDE.Compiler.Nodes;

public enum PinDirection
{
    Input,
    Output,
}

/// <summary>
/// A pin declared by a node definition. A pin without a <see cref="DataType"/> carries execution flow.
/// </summary>
public sealed record PinDefinition(string Id, string DisplayName, PinDirection Direction, TypeRef? DataType)
{
    public bool IsExecution => DataType is null;

    /// <summary>
    /// True for an execution output whose statements form a nested block, such as a loop body or the Try and Catch blocks of a
    /// Try node. The block ends where that flow ends, and the node itself continues through its other execution outputs.
    /// </summary>
    public bool StartsBlock { get; init; }

    /// <summary>True for the execution output of a loop node that runs the loop body, a nested block that may use Break and Continue.</summary>
    public bool StartsLoopBody { get; init; }

    public static PinDefinition ExecutionIn(string id = "exec", string displayName = "") =>
        new(id, displayName, PinDirection.Input, null);

    public static PinDefinition ExecutionOut(string id = "then", string displayName = "") =>
        new(id, displayName, PinDirection.Output, null);

    public static PinDefinition LoopBody(string id = "body", string displayName = "Loop Body") =>
        new(id, displayName, PinDirection.Output, null) { StartsBlock = true, StartsLoopBody = true };

    public static PinDefinition Block(string id, string displayName) =>
        new(id, displayName, PinDirection.Output, null) { StartsBlock = true };

    public static PinDefinition DataIn(string id, string displayName, TypeRef type) =>
        new(id, displayName, PinDirection.Input, type);

    public static PinDefinition DataOut(string id, string displayName, TypeRef type) =>
        new(id, displayName, PinDirection.Output, type);
}

/// <summary>An editable value stored in <c>GraphNode.Properties</c>, such as a literal.</summary>
public sealed record PropertyDefinition(string Id, string DisplayName, TypeRef Type, string DefaultValue);
