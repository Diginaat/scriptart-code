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
    /// True for the execution output of a loop node that runs the loop body. The statements reached through it form a nested
    /// region: an iteration ends where that flow ends, and the loop node itself continues through its other execution output.
    /// </summary>
    public bool StartsLoopBody { get; init; }

    public static PinDefinition ExecutionIn(string id = "exec", string displayName = "") =>
        new(id, displayName, PinDirection.Input, null);

    public static PinDefinition ExecutionOut(string id = "then", string displayName = "") =>
        new(id, displayName, PinDirection.Output, null);

    public static PinDefinition LoopBody(string id = "body", string displayName = "Loop Body") =>
        new(id, displayName, PinDirection.Output, null) { StartsLoopBody = true };

    public static PinDefinition DataIn(string id, string displayName, TypeRef type) =>
        new(id, displayName, PinDirection.Input, type);

    public static PinDefinition DataOut(string id, string displayName, TypeRef type) =>
        new(id, displayName, PinDirection.Output, type);
}

/// <summary>An editable value stored in <c>GraphNode.Properties</c>, such as a literal.</summary>
public sealed record PropertyDefinition(string Id, string DisplayName, TypeRef Type, string DefaultValue);
