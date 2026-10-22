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

    /// <summary>The input of a method's call node that takes the object or value whose method runs.</summary>
    public const string TargetPin = "target";

    public const string Category = "Functions";
}

/// <summary>
/// A function graph's signature, with every type resolved. Parameter ids double as pin ids. <see cref="Owner"/> is the class or
/// struct of a method or constructor, and null for a function of the program class. The owner of a method of a generic type keeps
/// its type parameters (<c>Box&lt;T&gt;</c>) until <see cref="Instantiate"/> gives them arguments.
/// </summary>
public sealed record FunctionSignature(
    string GraphId,
    string Name,
    IReadOnlyList<(string Id, string Name, TypeRef Type)> Parameters,
    TypeRef ReturnType,
    TypeRef? Owner = null,
    bool IsConstructor = false)
{
    /// <summary>
    /// The signature of <paramref name="graph"/>, whose types and owner are resolved against <paramref name="projectGraphs"/>; null
    /// when it is not a function or constructor, uses an unsupported type, or belongs to a class or struct that is not in the project.
    /// A constructor always has an owner and returns nothing.
    /// </summary>
    public static FunctionSignature? Of(GraphDocument graph, IReadOnlyList<GraphDocument>? projectGraphs = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        bool isConstructor = graph.Kind == GraphKind.Constructor;
        if (graph.Kind != GraphKind.Function && !isConstructor)
        {
            return null;
        }

        TypeRef? owner = null;
        if (graph.Owner is not null)
        {
            if ((projectGraphs ?? []).FirstOrDefault(type => type.Id == graph.Owner && TypeRef.IsTypeGraph(type)) is not { } ownerGraph)
            {
                return null;
            }

            owner = TypeRef.ProjectType(ownerGraph.Name, [.. ownerGraph.TypeParameters.Select(TypeRef.TypeParameter)]);
        }

        if (isConstructor && (owner is null || graph.ReturnType is not null))
        {
            return null;
        }

        TypeRef? returnType = graph.ReturnType is null ? TypeRef.Void : TypeRef.FromName(graph.ReturnType, projectGraphs, graph);
        List<(string, string, TypeRef)> parameters = [];
        foreach (GraphParameter parameter in graph.Parameters)
        {
            if (TypeRef.FromName(parameter.Type, projectGraphs, graph) is not { } type)
            {
                return null;
            }

            parameters.Add((parameter.Id, parameter.Name, type));
        }

        return returnType is null ? null : new FunctionSignature(graph.Id, graph.Name, parameters, returnType, owner, isConstructor);
    }

    /// <summary>
    /// This signature with <paramref name="typeArguments"/> for the type parameters of a generic owner, as seen by a caller of
    /// <c>Box&lt;int&gt;</c>; null when their number does not match. Unchanged for a signature whose owner is not generic.
    /// </summary>
    public FunctionSignature? Instantiate(IReadOnlyList<TypeRef> typeArguments)
    {
        ArgumentNullException.ThrowIfNull(typeArguments);
        IReadOnlyList<TypeRef> typeParameters = Owner?.Arguments ?? [];
        if (typeParameters.Count != typeArguments.Count)
        {
            return null;
        }

        if (typeParameters.Count == 0)
        {
            return this;
        }

        Dictionary<string, TypeRef> map = ProjectTypeDefinition.TypeArgumentMap([.. typeParameters.Select(parameter => parameter.Name)], typeArguments);
        return this with
        {
            Owner = Owner!.Substitute(map),
            ReturnType = ReturnType.Substitute(map),
            Parameters = [.. Parameters.Select(parameter => (parameter.Id, parameter.Name, parameter.Type.Substitute(map)))],
        };
    }

    /// <summary>A text form of everything the generated nodes depend on, used as a cache key.</summary>
    internal string Key => string.Join("\n", [GraphId, Name, ReturnType.SourceName, Owner?.SourceName ?? "", IsConstructor.ToString(), .. Parameters.Select(parameter => $"{parameter.Id}:{parameter.Name}:{parameter.Type.SourceName}")]);
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
/// Calls a function graph: one input per parameter and, when the function returns a value, a Result output. A method of a class or
/// struct also has a Target input, the object or value whose method runs. Always a statement node, because the function can have
/// side effects, so it runs exactly once and in execution order.
/// </summary>
public sealed class FunctionCallNode(FunctionSignature signature) : StatementNodeDefinition(
    FunctionNodes.CallTypeId,
    signature.Owner is null ? $"Call {signature.Name}" : $"Call {signature.Owner}.{signature.Name}",
    FunctionNodes.Category,
    (signature.Owner is null ? $"Runs the function {signature.Name}" : $"Runs the method {signature.Name} of a {signature.Owner}")
        + (signature.ReturnType == TypeRef.Void ? "." : $" and outputs the {signature.ReturnType} it returns."),
    [
        PinDefinition.ExecutionIn(),
        .. signature.Owner is null ? [] : new[] { PinDefinition.DataIn(FunctionNodes.TargetPin, "Target", signature.Owner) },
        .. signature.Parameters.Select(parameter => PinDefinition.DataIn(parameter.Id, parameter.Name, parameter.Type)),
        PinDefinition.ExecutionOut(),
        .. signature.ReturnType == TypeRef.Void ? [] : new[] { PinDefinition.DataOut("result", "Result", signature.ReturnType) },
    ])
{
    public FunctionSignature Signature { get; } = signature;

    public override IReadOnlyDictionary<string, string> InitialProperties { get; init; } = signature.Owner is { Arguments.Count: > 0 } owner
        ? new Dictionary<string, string> { [FunctionNodes.FunctionProperty] = signature.GraphId, [TypeNodes.TypeArgumentsProperty] = TypeRef.ArgumentNames(owner.Arguments) }
        : new Dictionary<string, string> { [FunctionNodes.FunctionProperty] = signature.GraphId };

    public override void Lower(StatementLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IrExpression[] arguments = [.. Signature.Parameters.Select(parameter => context.Input(parameter.Id))];
        IrExpression call = Signature.Owner is not null ? new IrInstanceCall(context.Input(FunctionNodes.TargetPin), Signature.Name, arguments, Signature.ReturnType)
            : context.FunctionHost is { } host ? new IrStaticCall(host, Signature.Name, arguments, Signature.ReturnType)
            : new IrCall(Signature.Name, arguments, Signature.ReturnType);

        // A function that waits for something is async, and its callers wait for it to finish (D-029).
        if (context.IsAsync(Signature.GraphId))
        {
            call = new IrAwait(call, Signature.ReturnType);
        }

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
