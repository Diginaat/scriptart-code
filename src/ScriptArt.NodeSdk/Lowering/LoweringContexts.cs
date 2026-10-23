using ScriptArt.Compiler.Ir;
using ScriptArt.Compiler.Nodes;

namespace ScriptArt.Compiler.Lowering;

/// <summary>
/// What a node definition can see while lowering one node instance to IR.
/// </summary>
public class ExpressionLoweringContext
{
    private protected ExpressionLoweringContext(ILoweringHost lowerer, ResolvedNode node)
    {
        Lowerer = lowerer;
        Node = node;
    }

    public string NodeId => Node.Id;

    private protected ILoweringHost Lowerer { get; }

    private protected ResolvedNode Node { get; }

    /// <summary>The expression connected to a data input. Reports a diagnostic and returns an error placeholder when the input is unconnected.</summary>
    public IrExpression Input(string pinId) => Lowerer.LowerInput(Node, pinId);

    /// <summary>The local that holds a graph variable. Only valid for a variable the graph declares.</summary>
    public IrLocal Variable(string variableId) => Lowerer.VariableLocal(variableId);

    /// <summary>The method parameter declared by a function graph's parameter.</summary>
    public IrLocal Parameter(string parameterId) => Lowerer.ParameterLocal(parameterId);

    /// <summary>The return type of the method being generated; <see cref="TypeSystem.TypeRef.Void"/> for the entry point.</summary>
    public TypeSystem.TypeRef ReturnType => Lowerer.ReturnType;

    /// <summary>The program class, when the graph is a method of a class or struct and a function call must name it; otherwise null.</summary>
    public TypeSystem.TypeRef? FunctionHost => Lowerer.FunctionHost;

    /// <summary>True when the function, method, or constructor graph <paramref name="graphId"/> generates an async method that callers await.</summary>
    public bool IsAsync(string graphId) => Lowerer.AsyncGraphs.Contains(graphId);

    /// <summary>The raw text of a property, or its default value when the node does not set it.</summary>
    public string Property(string propertyId)
    {
        PropertyDefinition property = Node.Definition.FindProperty(propertyId)
            ?? throw new ArgumentException($"Node type '{Node.Definition.TypeId}' has no property '{propertyId}'.", nameof(propertyId));
        return Node.Node.Properties.GetValueOrDefault(propertyId, property.DefaultValue);
    }

    /// <summary>The property parsed as a literal of its declared type.</summary>
    public IrExpression LiteralProperty(string propertyId)
    {
        PropertyDefinition property = Node.Definition.FindProperty(propertyId)
            ?? throw new ArgumentException($"Node type '{Node.Definition.TypeId}' has no property '{propertyId}'.", nameof(propertyId));

        // Invalid values were reported while building the graph index.
        return LiteralValues.TryParse(property.Type, Property(propertyId), out object? value, out _)
            ? new IrLiteral(value, property.Type)
            : new IrError(property.Type);
    }

    internal static ExpressionLoweringContext Create(ILoweringHost lowerer, ResolvedNode node) => new(lowerer, node);
}

/// <summary>
/// What a statement node definition can do while lowering: read inputs, emit statements, declare outputs, and lower nested flow.
/// </summary>
public sealed class StatementLoweringContext : ExpressionLoweringContext
{
    private readonly IrBlock block;

    internal StatementLoweringContext(ILoweringHost lowerer, ResolvedNode node, IrBlock block)
        : base(lowerer, node)
    {
        this.block = block;
    }

    /// <summary>Appends a statement attributed to this node.</summary>
    public void Emit(IrStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        block.Statements.Add(statement with { SourceNodeId = NodeId });
    }

    /// <summary>
    /// Makes <paramref name="value"/> available through a data output. When the output is connected, the value is stored in a
    /// new local named after <paramref name="nameHint"/>; otherwise <paramref name="whenUnused"/> (if any) is emitted as a statement.
    /// </summary>
    public void DeclareOutput(string pinId, IrExpression value, string nameHint, IrExpression? whenUnused = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Lowerer.IsOutputUsed(Node, pinId))
        {
            IrLocal local = Lowerer.DeclareLocal(Node, pinId, nameHint, value.Type);
            Emit(new IrLocalDeclaration(local, value));
        }
        else if (whenUnused is not null)
        {
            Emit(new IrExpressionStatement(whenUnused));
        }
    }

    /// <summary>Makes an existing local, such as a parameter, available through a data output without declaring anything.</summary>
    public void BindOutput(string pinId, IrLocal local) => Lowerer.BindLocal(Node, pinId, local);

    /// <summary>
    /// Lowers the statements reached through an execution output up to the point where this node's paths reconverge.
    /// </summary>
    public IrBlock LowerBranch(string executionPinId) => Lowerer.LowerBranch(Node, executionPinId);

    /// <summary>
    /// Lowers the loop body reached through <paramref name="executionPinId"/>, a pin created with <see cref="PinDefinition.LoopBody"/>.
    /// Each of <paramref name="loopValues"/> makes a data output of this node available inside the body only.
    /// </summary>
    public IrBlock LowerLoopBody(string executionPinId, params IReadOnlyList<(string PinId, IrLocal Local)> loopValues) =>
        Lowerer.LowerLoopBody(Node, executionPinId, loopValues);

    /// <summary>
    /// Lowers the nested block reached through <paramref name="executionPinId"/>, a pin created with <see cref="PinDefinition.Block"/>.
    /// Each of <paramref name="values"/> makes a data output of this node available inside the block only.
    /// </summary>
    public IrBlock LowerBlock(string executionPinId, NestedBlock kind, params IReadOnlyList<(string PinId, IrLocal Local)> values) =>
        Lowerer.LowerNestedBlock(Node, executionPinId, kind, values);

    /// <summary>True when a data output of this node is connected.</summary>
    public bool IsOutputUsed(string pinId) => Lowerer.IsOutputUsed(Node, pinId);

    /// <summary>A new local with a unique name, not yet bound to any pin.</summary>
    public IrLocal NewLocal(string nameHint, TypeSystem.TypeRef type) => Lowerer.NewLocal(nameHint, type);

    /// <summary>True while lowering the body of a loop, where <c>break</c> and <c>continue</c> are allowed.</summary>
    public bool IsInLoopBody => Lowerer.IsInLoopBody;

    /// <summary>True while lowering a Finally block, which Return cannot leave.</summary>
    public bool IsInFinally => Lowerer.IsInFinally;

    /// <summary>True while lowering a Catch block, where Rethrow is allowed.</summary>
    public bool IsInCatch => Lowerer.IsInCatch;

    /// <summary>Reports an error on this node. The graph then generates no code.</summary>
    public void ReportError(string diagnosticId, string message) => Lowerer.ReportError(Node, diagnosticId, message);
}
