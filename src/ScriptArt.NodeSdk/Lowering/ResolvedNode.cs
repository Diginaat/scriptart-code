using ScriptArt.Compiler.Nodes;
using ScriptArt.GraphModel;

namespace ScriptArt.Compiler.Lowering;

/// <summary>A graph node paired with its definition.</summary>
public sealed record ResolvedNode(GraphNode Node, NodeDefinition Definition)
{
    public string Id => Node.Id;
}

/// <summary>The kinds of nested block. They decide where Break, Continue, and Return are allowed.</summary>
public enum NestedBlock
{
    LoopBody,
    Try,
    Catch,
    Finally,

    /// <summary>A branch of an if statement that names a value only inside it, such as Cast To's Succeeded.</summary>
    Conditional,

    /// <summary>
    /// The body of a handler that runs later, such as a signal's: a lambda. Break, Continue, and Rethrow cannot leave it, and Return
    /// ends the handler, not the method.
    /// </summary>
    Handler,
}

/// <summary>
/// What lowering contexts delegate to: the compiler's lowering of one method. Implemented by the compiler; node libraries see only
/// <see cref="ExpressionLoweringContext"/> and <see cref="StatementLoweringContext"/>.
/// </summary>
internal interface ILoweringHost
{
    IReadOnlySet<string> AsyncGraphs { get; }

    TypeSystem.TypeRef ReturnType { get; }

    TypeSystem.TypeRef? FunctionHost { get; }

    bool IsInLoopBody { get; }

    bool IsInFinally { get; }

    bool IsInCatch { get; }

    Ir.IrExpression LowerInput(ResolvedNode node, string pinId);

    Ir.IrLocal VariableLocal(string variableId);

    Ir.IrLocal ParameterLocal(string parameterId);

    bool IsOutputUsed(ResolvedNode node, string pinId);

    Ir.IrLocal DeclareLocal(ResolvedNode node, string pinId, string nameHint, TypeSystem.TypeRef type);

    void BindLocal(ResolvedNode node, string pinId, Ir.IrLocal local);

    Ir.IrBlock LowerBranch(ResolvedNode node, string executionPinId);

    Ir.IrBlock LowerLoopBody(ResolvedNode node, string executionPinId, IReadOnlyList<(string PinId, Ir.IrLocal Local)> loopValues);

    Ir.IrBlock LowerNestedBlock(ResolvedNode node, string executionPinId, NestedBlock kind, IReadOnlyList<(string PinId, Ir.IrLocal Local)> values);

    Ir.IrLocal NewLocal(string nameHint, TypeSystem.TypeRef type);

    void ReportError(ResolvedNode node, string diagnosticId, string message);
}
