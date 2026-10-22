using System.Globalization;
using VisualStudioLikeIDE.Compiler.Ir;
using VisualStudioLikeIDE.Compiler.Lowering;
using VisualStudioLikeIDE.Compiler.TypeSystem;
using VisualStudioLikeIDE.GraphModel;

namespace VisualStudioLikeIDE.Compiler.Nodes;

/// <summary>
/// Nodes for function graphs. Like variable nodes they are not registered: <see cref="NodeLibrary.Resolve"/> builds them from a
/// function's signature, so their pins follow the function's parameters and return type.
/// </summary>
public static class FunctionNodes
{
    /// <summary>Where a function starts; one data output per parameter.</summary>
    public const string EntryTypeId = "function.entry";

    /// <summary>Returns a value from a function that has a return type.</summary>
    public const string ReturnTypeId = "function.return";

    /// <summary>Calls the function graph named by <see cref="FunctionProperty"/>.</summary>
    public const string CallTypeId = "function.call";

    /// <summary>The call node property holding the id of the function graph it calls.</summary>
    public const string FunctionProperty = "function";

    public const string Category = "Functions";
}

/// <summary>A function graph's signature, with every type resolved. Parameter ids double as pin ids.</summary>
public sealed record FunctionSignature(string GraphId, string Name, IReadOnlyList<(string Id, string Name, TypeRef Type)> Parameters, TypeRef ReturnType)
{
    /// <summary>
    /// The signature of <paramref name="graph"/>, whose types are resolved against <paramref name="projectGraphs"/>; null when it is
    /// not a function or uses an unsupported type.
    /// </summary>
    public static FunctionSignature? Of(GraphDocument graph, IReadOnlyList<GraphDocument>? projectGraphs = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        TypeRef? returnType = graph.ReturnType is null ? TypeRef.Void : TypeRef.FromName(graph.ReturnType, projectGraphs);
        List<(string, string, TypeRef)> parameters = [];
        foreach (GraphParameter parameter in graph.Parameters)
        {
            if (TypeRef.FromName(parameter.Type, projectGraphs) is not { } type)
            {
                return null;
            }

            parameters.Add((parameter.Id, parameter.Name, type));
        }

        return graph.Kind == GraphKind.Function && returnType is not null ? new FunctionSignature(graph.Id, graph.Name, parameters, returnType) : null;
    }

    /// <summary>A text form of everything the generated nodes depend on, used as a cache key.</summary>
    internal string Key => string.Join("\n", [GraphId, Name, ReturnType.DisplayName, .. Parameters.Select(parameter => $"{parameter.Id}:{parameter.Name}:{parameter.Type}")]);
}

/// <summary>The start of a function. Its outputs are the function's parameters.</summary>
public sealed class FunctionEntryNode(FunctionSignature signature) : StatementNodeDefinition(
    FunctionNodes.EntryTypeId,
    "Function Entry",
    FunctionNodes.Category,
    $"Where {signature.Name} starts. Its outputs are the values passed by the caller.",
    [PinDefinition.ExecutionOut(), .. signature.Parameters.Select(parameter => PinDefinition.DataOut(parameter.Id, parameter.Name, parameter.Type))])
{
    public FunctionSignature Signature { get; } = signature;

    public override void Lower(StatementLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach ((string id, _, _) in Signature.Parameters)
        {
            context.BindOutput(id, context.Parameter(id));
        }
    }
}

/// <summary>Ends a function and returns a value. Generates <c>return value;</c>.</summary>
public sealed class FunctionReturnNode(TypeRef type) : StatementNodeDefinition(
    FunctionNodes.ReturnTypeId,
    "Return Value",
    FunctionNodes.Category,
    $"Stops the function and gives the {type} value back to the caller.",
    [PinDefinition.ExecutionIn(), PinDefinition.DataIn("value", "Value", type)])
{
    public override void Lower(StatementLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.IsInFinally)
        {
            context.ReportError(Diagnostics.DiagnosticIds.JumpOutOfFinally, "Return Value cannot leave a Finally block. Put it after the Try node.");
            return;
        }

        context.Emit(new IrReturn(context.Input("value")));
    }
}

/// <summary>
/// Calls a function graph: one input per parameter and, when the function returns a value, a Result output. Always a statement
/// node, because the function can have side effects, so it runs exactly once and in execution order.
/// </summary>
public sealed class FunctionCallNode(FunctionSignature signature) : StatementNodeDefinition(
    FunctionNodes.CallTypeId,
    $"Call {signature.Name}",
    FunctionNodes.Category,
    signature.ReturnType == TypeRef.Void
        ? $"Runs the function {signature.Name}."
        : $"Runs the function {signature.Name} and outputs the {signature.ReturnType} it returns.",
    [
        PinDefinition.ExecutionIn(),
        .. signature.Parameters.Select(parameter => PinDefinition.DataIn(parameter.Id, parameter.Name, parameter.Type)),
        PinDefinition.ExecutionOut(),
        .. signature.ReturnType == TypeRef.Void ? [] : new[] { PinDefinition.DataOut("result", "Result", signature.ReturnType) },
    ])
{
    public FunctionSignature Signature { get; } = signature;

    public override IReadOnlyDictionary<string, string> InitialProperties { get; } =
        new Dictionary<string, string> { [FunctionNodes.FunctionProperty] = signature.GraphId };

    public override void Lower(StatementLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var call = new IrCall(Signature.Name, [.. Signature.Parameters.Select(parameter => context.Input(parameter.Id))], Signature.ReturnType);
        if (Signature.ReturnType == TypeRef.Void)
        {
            context.Emit(new IrExpressionStatement(call));
            return;
        }

        // The result is named after the function (Square → square); a lower-case function name would be hidden by such a local.
        string name = Signature.Name;
        string hint = char.IsUpper(name[0]) ? char.ToLower(name[0], CultureInfo.InvariantCulture) + name[1..] : name + "Result";
        context.DeclareOutput("result", call, hint, whenUnused: call);
    }
}
