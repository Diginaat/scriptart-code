using ScriptArt.Compiler.Ir;
using ScriptArt.Compiler.Lowering;
using ScriptArt.Compiler.TypeSystem;
using ScriptArt.GraphModel;

namespace ScriptArt.Compiler.Nodes;

/// <summary>
/// Nodes for the Godot signals a class of the project declares (D-051). Like type nodes they are built by
/// <see cref="NodeLibrary.Resolve"/> from the class graph, so their pins follow the signal's parameters. They name the signal with a
/// string, not Godot's generated <c>SignalName</c>, so the code compiles whether or not Godot's source generators have run.
/// </summary>
public static class SignalNodes
{
    /// <summary>Emits the signal named by <see cref="SignalProperty"/> of the class named by <see cref="TypeNodes.TypeProperty"/>.</summary>
    public const string EmitTypeId = "signal.emit";

    /// <summary>Runs its Handler each time the signal is emitted.</summary>
    public const string OnTypeId = "signal.on";

    /// <summary>The node property holding the id of the signal in its class graph.</summary>
    public const string SignalProperty = "signal";

    public const string Category = "Signals";

    private static readonly TypeRef Callable = TypeRef.External("Godot", "Callable");

    /// <summary>True for the node types whose pins come from a signal of a class.</summary>
    public static bool IsSignalNode(string typeId) => typeId is EmitTypeId or OnTypeId;

    /// <summary>The signal as nodes use it, with its parameter types resolved; null when the class or signal is gone or a type is unsupported.</summary>
    public static ProjectSignal? Of(GraphDocument type, string? signalId, IReadOnlyList<GraphDocument> projectGraphs)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.Kind != GraphKind.Class || type.Signals.Find(signal => signal.Id == signalId) is not { } signal)
        {
            return null;
        }

        List<(string, string, TypeRef)> parameters = [];
        foreach (GraphParameter parameter in signal.Parameters)
        {
            if (TypeRef.FromName(parameter.Type, projectGraphs, type) is not { } parameterType)
            {
                return null;
            }

            parameters.Add((parameter.Id, parameter.Name, parameterType));
        }

        return new ProjectSignal(type.Id, TypeRef.ProjectType(type.Name), signal.Id, signal.Name, parameters);
    }

    /// <summary>The Emit and On nodes of every signal of <paramref name="type"/>.</summary>
    internal static IEnumerable<NodeDefinition> For(GraphDocument type, IReadOnlyList<GraphDocument> projectGraphs) =>
        type.Signals.Select(signal => Of(type, signal.Id, projectGraphs)).OfType<ProjectSignal>()
            .SelectMany(signal => new NodeDefinition[] { new EmitSignalNode(signal), new OnSignalNode(signal) });

    /// <summary><c>Callable.From((int damage) =&gt; { … })</c>: the handler as Godot's <c>Connect</c> takes it.</summary>
    internal static IrExpression Handler(IrLambda lambda) => new IrStaticCall(Callable, "From", [lambda], Callable);

    internal static IReadOnlyDictionary<string, string> Properties(ProjectSignal signal) =>
        new Dictionary<string, string> { [TypeNodes.TypeProperty] = signal.TypeGraphId, [SignalProperty] = signal.SignalId };
}

/// <summary>A signal of a class of the project, with its parameters' ids, names, and resolved types.</summary>
public sealed record ProjectSignal(string TypeGraphId, TypeRef Owner, string SignalId, string Name, IReadOnlyList<(string Id, string Name, TypeRef Type)> Parameters);

/// <summary>Emit: <c>instance.EmitSignal("Hit", damage);</c>. Every handler connected to the signal runs.</summary>
public sealed class EmitSignalNode(ProjectSignal signal) : StatementNodeDefinition(
    SignalNodes.EmitTypeId,
    $"Emit {signal.Owner.DisplayName}.{signal.Name}",
    SignalNodes.Category,
    $"Emits the signal {signal.Name} of a {signal.Owner.DisplayName}, which runs every handler connected to it, in Godot and in On nodes. Generates EmitSignal(\"{signal.Name}\", …).",
    [
        PinDefinition.ExecutionIn(),
        PinDefinition.DataIn("instance", signal.Owner.DisplayName, signal.Owner),
        .. signal.Parameters.Select(parameter => PinDefinition.DataIn(parameter.Id, parameter.Name, parameter.Type)),
        PinDefinition.ExecutionOut(),
    ])
{
    public override IReadOnlyDictionary<string, string> InitialProperties { get; init; } = SignalNodes.Properties(signal);

    public override void Lower(StatementLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IrExpression[] arguments = [new IrLiteral(signal.Name, TypeRef.String), .. signal.Parameters.Select(parameter => context.Input(parameter.Id))];
        context.Emit(new IrExpressionStatement(new IrInstanceCall(context.Input("instance"), "EmitSignal", arguments, TypeRef.Void)));
    }
}

/// <summary>
/// On: <c>instance.Connect("Hit", Callable.From((int damage) =&gt; { handler }));</c>. The signal's values are outputs inside Handler only.
/// </summary>
public sealed class OnSignalNode(ProjectSignal signal) : StatementNodeDefinition(
    SignalNodes.OnTypeId,
    $"On {signal.Owner.DisplayName}.{signal.Name}",
    SignalNodes.Category,
    $"Runs Handler each time the {signal.Owner.DisplayName} emits {signal.Name}, from then on, with the values it emits. Generates Connect(\"{signal.Name}\", Callable.From(…)).",
    [
        PinDefinition.ExecutionIn(),
        PinDefinition.DataIn("instance", signal.Owner.DisplayName, signal.Owner),
        PinDefinition.Block("handler", "Handler"),
        .. signal.Parameters.Select(parameter => PinDefinition.DataOut(parameter.Id, parameter.Name, parameter.Type)),
        PinDefinition.ExecutionOut(),
    ])
{
    public override IReadOnlyDictionary<string, string> InitialProperties { get; init; } = SignalNodes.Properties(signal);

    public override void Lower(StatementLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IrExpression instance = context.Input("instance");
        IrLocal[] received = [.. signal.Parameters.Select(parameter => context.NewLocal(parameter.Name, parameter.Type))];
        IrBlock handler = context.LowerBlock("handler", NestedBlock.Handler, [.. signal.Parameters.Select((parameter, i) => (parameter.Id, received[i]))]);
        IrExpression connect = new IrInstanceCall(instance, "Connect",
            [new IrLiteral(signal.Name, TypeRef.String), SignalNodes.Handler(new IrLambda(received, handler, TypedParameters: true))], TypeRef.Void);
        context.Emit(new IrExpressionStatement(connect));
    }
}
