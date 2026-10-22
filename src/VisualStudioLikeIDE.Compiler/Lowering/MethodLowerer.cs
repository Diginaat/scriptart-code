using Microsoft.CodeAnalysis.CSharp;
using VisualStudioLikeIDE.Compiler.Diagnostics;
using VisualStudioLikeIDE.Compiler.Ir;
using VisualStudioLikeIDE.Compiler.Nodes;
using VisualStudioLikeIDE.GraphModel;

namespace VisualStudioLikeIDE.Compiler.Lowering;

/// <summary>
/// Lowers the execution flow of one method graph into a structured IR block.
/// </summary>
/// <remarks>
/// Statement nodes are visited in execution order. After each node, lowering continues at its immediate post-dominator,
/// which turns reconverging branches into ordinary <c>if</c>/<c>else</c> statements followed by the shared continuation.
/// Pure nodes are inlined as expressions where they are used. Values produced by statement nodes live in locals and are
/// only visible in the block that declares them and its nested blocks, matching C# scoping.
/// </remarks>
internal sealed class MethodLowerer
{
    private readonly GraphIndex index;
    private readonly ExecutionFlow flow;
    private readonly List<GraphDiagnostic> diagnostics;
    private readonly HashSet<string> emittedStatements = new(StringComparer.Ordinal);
    private readonly HashSet<string> loweredPureNodes = new(StringComparer.Ordinal);
    private readonly HashSet<string> pureNodesInProgress = new(StringComparer.Ordinal);
    private readonly HashSet<string> usedNames = new(StringComparer.Ordinal);
    private readonly List<Dictionary<PinRef, IrLocal>> scopes = [];
    private readonly Dictionary<string, IrLocal> variables = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IrLocal> parameters = new(StringComparer.Ordinal);
    private int loopDepth;

    private MethodLowerer(GraphIndex index, ExecutionFlow flow, List<GraphDiagnostic> diagnostics, TypeSystem.TypeRef returnType)
    {
        this.index = index;
        this.flow = flow;
        this.diagnostics = diagnostics;
        ReturnType = returnType;
    }

    internal TypeSystem.TypeRef ReturnType { get; }

    private string GraphId => index.Graph.Id;

    /// <summary>
    /// Lowers everything reachable from <paramref name="entry"/>. Returns null when the execution flow cannot be analyzed.
    /// </summary>
    internal static IrBlock? LowerBody(GraphIndex index, ResolvedNode entry, List<GraphDiagnostic> diagnostics, TypeSystem.TypeRef returnType)
    {
        ExecutionFlow? flow = ExecutionFlow.Analyze(index, entry, diagnostics);
        if (flow is null)
        {
            return null;
        }

        var lowerer = new MethodLowerer(index, flow, diagnostics, returnType);

        // Parameter names come first: they are part of the method's signature and must not be renamed.
        foreach (GraphParameter parameter in index.Graph.Parameters)
        {
            if (TypeSystem.TypeRef.FromKeyword(parameter.Type) is { } type)
            {
                lowerer.parameters.Add(parameter.Id, new IrLocal(lowerer.AllocateName(parameter.Name), type));
            }
        }

        List<IrStatement> declarations = lowerer.DeclareVariables();
        IrBlock body = lowerer.LowerRegion(entry, stop: null);
        body.Statements.InsertRange(0, declarations);
        lowerer.ReportUnusedNodes();
        return body;
    }

    /// <summary>The parameters of the method in declaration order, as generated.</summary>
    internal static IReadOnlyList<IrParameter> Parameters(GraphDocument graph) =>
        [.. graph.Parameters
            .Select(parameter => (parameter.Name, Type: TypeSystem.TypeRef.FromKeyword(parameter.Type)))
            .Where(parameter => parameter.Type is not null)
            .Select(parameter => new IrParameter(parameter.Name, parameter.Type!))];

    internal IrLocal ParameterLocal(string parameterId) =>
        parameters.TryGetValue(parameterId, out IrLocal? local)
            ? local
            : throw new ArgumentException($"The graph declares no parameter '{parameterId}'.", nameof(parameterId));

    internal void BindLocal(ResolvedNode node, string pinId, IrLocal local) => scopes[^1].Add(new PinRef(node.Id, pinId), local);

    internal IrLocal VariableLocal(string variableId) =>
        variables.TryGetValue(variableId, out IrLocal? local)
            ? local
            : throw new ArgumentException($"The graph declares no variable '{variableId}'.", nameof(variableId));

    /// <summary>
    /// Declares a local, at the top of the method, for each variable some node uses. Variable names are reserved before any
    /// generated local is named, so they come out exactly as the user wrote them.
    /// </summary>
    private List<IrStatement> DeclareVariables()
    {
        HashSet<string> read = [.. index.Nodes.Select(node => node.Definition).OfType<VariableGetNode>().Select(get => get.VariableId)];
        ResolvedNode[] writers = [.. index.Nodes.Where(node => node.Definition is VariableSetNode)];
        List<IrStatement> declarations = [];
        foreach (GraphVariable variable in index.Graph.Variables)
        {
            ResolvedNode? firstWriter = writers.FirstOrDefault(node => ((VariableSetNode)node.Definition).VariableId == variable.Id);
            if ((!read.Contains(variable.Id) && firstWriter is null) || TypeSystem.TypeRef.FromKeyword(variable.Type) is not { } type)
            {
                continue;
            }

            if (!read.Contains(variable.Id))
            {
                diagnostics.Add(new(DiagnosticIds.VariableNeverRead, DiagnosticSeverity.Warning,
                    $"Variable '{variable.Name}' is set but never read.", GraphId, firstWriter!.Id));
            }

            var local = new IrLocal(AllocateName(variable.Name), type);
            variables.Add(variable.Id, local);

            // An invalid initial value was reported while validating the graph's variables.
            IrExpression initial = LiteralValues.TryParse(type, variable.Value, out object? value, out _) ? new IrLiteral(value, type) : new IrError(type);
            declarations.Add(new IrLocalDeclaration(local, initial));
        }

        return declarations;
    }

    internal IrBlock LowerBranch(ResolvedNode node, string executionPinId)
    {
        PinDefinition pin = node.Definition.FindPin(executionPinId)
            ?? throw new ArgumentException($"Node type '{node.Definition.TypeId}' has no pin '{executionPinId}'.", nameof(executionPinId));
        if (!pin.IsExecution || pin.Direction != PinDirection.Output)
        {
            throw new ArgumentException($"Pin '{executionPinId}' is not an execution output.", nameof(executionPinId));
        }

        ResolvedNode? target = index.ExecutionTarget(node.Id, executionPinId);
        ResolvedNode? reconvergence = flow.ImmediatePostDominator(node);
        return target is null || target == reconvergence ? new IrBlock() : LowerRegion(target, reconvergence);
    }

    internal bool IsInLoopBody => loopDepth > 0;

    /// <summary>
    /// Lowers the body of a loop node. <paramref name="loopValues"/> are the node's data outputs that only exist inside the body,
    /// such as a For Loop's index.
    /// </summary>
    internal IrBlock LowerLoopBody(ResolvedNode node, string executionPinId, IReadOnlyList<(string PinId, IrLocal Local)> loopValues)
    {
        PinDefinition pin = node.Definition.FindPin(executionPinId)
            ?? throw new ArgumentException($"Node type '{node.Definition.TypeId}' has no pin '{executionPinId}'.", nameof(executionPinId));
        if (!pin.StartsLoopBody)
        {
            throw new ArgumentException($"Pin '{executionPinId}' does not start a loop body.", nameof(executionPinId));
        }

        if (index.ExecutionTarget(node.Id, executionPinId) is not { } target)
        {
            return new IrBlock();
        }

        scopes.Add(loopValues.ToDictionary(value => new PinRef(node.Id, value.PinId), value => value.Local));
        loopDepth++;
        IrBlock body = LowerRegion(target, stop: null);
        loopDepth--;
        scopes.RemoveAt(scopes.Count - 1);
        return body;
    }

    internal IrLocal NewLocal(string nameHint, TypeSystem.TypeRef type) => new(AllocateName(nameHint), type);

    internal void ReportError(ResolvedNode node, string diagnosticId, string message) => Report(diagnosticId, message, node.Id);

    internal IrExpression LowerInput(ResolvedNode node, string pinId)
    {
        PinDefinition pin = node.Definition.FindPin(pinId)
            ?? throw new ArgumentException($"Node type '{node.Definition.TypeId}' has no pin '{pinId}'.", nameof(pinId));
        if (pin.DataType is null || pin.Direction != PinDirection.Input)
        {
            throw new ArgumentException($"Pin '{pinId}' is not a data input.", nameof(pinId));
        }

        var input = new PinRef(node.Id, pinId);
        IReadOnlyList<GraphConnection> connections = index.ConnectionsTo(input);
        if (connections.Count == 0)
        {
            Report(DiagnosticIds.UnconnectedInput, $"{node.Definition.DisplayName}: input '{pin.DisplayName}' is not connected.", node.Id, input);
            return new IrError(pin.DataType);
        }

        PinRef output = connections[0].From;
        ResolvedNode source = index.Find(output.Node)!;
        IrExpression value = source.Definition is PureNodeDefinition pure
            ? LowerPureNode(source, pure)
            : LookUpLocal(output, input);

        return value.Type == pin.DataType || value is IrError ? value : new IrConvert(value, pin.DataType);
    }

    internal bool IsOutputUsed(ResolvedNode node, string pinId) =>
        index.ConnectionsFrom(new PinRef(node.Id, pinId)).Count > 0;

    internal IrLocal DeclareLocal(ResolvedNode node, string pinId, string nameHint, TypeSystem.TypeRef type)
    {
        var local = new IrLocal(AllocateName(nameHint), type);
        scopes[^1].Add(new PinRef(node.Id, pinId), local);
        return local;
    }

    private IrBlock LowerRegion(ResolvedNode start, ResolvedNode? stop)
    {
        var block = new IrBlock();
        scopes.Add([]);
        ResolvedNode? current = start;
        while (current is not null && current != stop)
        {
            if (!emittedStatements.Add(current.Id))
            {
                Report(DiagnosticIds.UnstructuredFlow,
                    $"{current.Definition.DisplayName} is reached from inside a branch or loop body and from outside it. Connect branch arms so they rejoin at a single node, and keep a loop body's nodes separate from what follows the loop.",
                    current.Id);
                break;
            }

            if (current.Definition is StatementNodeDefinition statement)
            {
                statement.Lower(new StatementLoweringContext(this, current, block));
            }

            current = flow.ImmediatePostDominator(current);
        }

        scopes.RemoveAt(scopes.Count - 1);
        return block;
    }

    private IrExpression LowerPureNode(ResolvedNode node, PureNodeDefinition definition)
    {
        if (!pureNodesInProgress.Add(node.Id))
        {
            Report(DiagnosticIds.DataCycle, $"{definition.DisplayName} depends on its own result.", node.Id);
            return new IrError(definition.Output.DataType!);
        }

        loweredPureNodes.Add(node.Id);
        IrExpression expression = definition.Lower(ExpressionLoweringContext.Create(this, node));
        pureNodesInProgress.Remove(node.Id);
        return expression;
    }

    private IrExpression LookUpLocal(PinRef output, PinRef consumer)
    {
        for (int i = scopes.Count - 1; i >= 0; i--)
        {
            if (scopes[i].TryGetValue(output, out IrLocal? local))
            {
                return new IrLocalReference(local);
            }
        }

        ResolvedNode source = index.Find(output.Node)!;
        PinDefinition pin = source.Definition.FindPin(output.Pin)!;
        Report(DiagnosticIds.ValueNotAvailable,
            $"'{pin.DisplayName}' from {source.Definition.DisplayName} is used before that node runs, or on a path where it does not run.",
            consumer.Node, consumer);
        return new IrError(pin.DataType!);
    }

    private string AllocateName(string hint)
    {
        string baseName = SyntaxFacts.IsValidIdentifier(hint) && SyntaxFacts.GetKeywordKind(hint) == SyntaxKind.None ? hint : "value";
        string name = baseName;
        for (int suffix = 2; !usedNames.Add(name); suffix++)
        {
            name = baseName + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return name;
    }

    private void ReportUnusedNodes()
    {
        foreach (ResolvedNode node in index.Nodes)
        {
            if (node.Definition is StatementNodeDefinition && !flow.IsReachable(node.Id))
            {
                diagnostics.Add(new(DiagnosticIds.UnreachableNode, DiagnosticSeverity.Warning,
                    $"{node.Definition.DisplayName} is not connected to the execution flow and generates no code.", GraphId, node.Id));
            }
            else if (node.Definition is PureNodeDefinition && !loweredPureNodes.Contains(node.Id))
            {
                diagnostics.Add(new(DiagnosticIds.UnusedNode, DiagnosticSeverity.Warning,
                    $"The result of {node.Definition.DisplayName} is not used.", GraphId, node.Id));
            }
        }
    }

    private void Report(string id, string message, string nodeId, PinRef? pin = null) =>
        diagnostics.Add(new(id, DiagnosticSeverity.Error, message, GraphId, nodeId, pin));
}
