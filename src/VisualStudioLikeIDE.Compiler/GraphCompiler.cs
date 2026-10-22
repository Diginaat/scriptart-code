using Microsoft.CodeAnalysis.CSharp;
using VisualStudioLikeIDE.Compiler.Diagnostics;
using VisualStudioLikeIDE.Compiler.Emit;
using VisualStudioLikeIDE.Compiler.Ir;
using VisualStudioLikeIDE.Compiler.Lowering;
using VisualStudioLikeIDE.Compiler.Nodes;
using VisualStudioLikeIDE.Compiler.TypeSystem;
using VisualStudioLikeIDE.GraphModel;

namespace VisualStudioLikeIDE.Compiler;

/// <summary>Settings shared by every graph in a project.</summary>
/// <param name="RootNamespace">Namespace of the generated types.</param>
/// <param name="Graphs">
/// The project's graphs. Call nodes refer to its functions, which become methods of <paramref name="HostClass"/>, and types name
/// its classes and structs.
/// </param>
/// <param name="HostClass">The class generated from the entry graph, which also holds every function.</param>
public sealed record GraphCompilationOptions(string RootNamespace, IReadOnlyList<GraphDocument>? Graphs = null, string HostClass = "Program")
{
    public IReadOnlyList<GraphDocument> Graphs { get; init; } = Graphs ?? [];

    /// <summary>The function graphs among <see cref="Graphs"/>.</summary>
    public IReadOnlyList<GraphDocument> Functions => [.. Graphs.Where(graph => graph.Kind == GraphKind.Function)];
}

/// <summary>
/// The outcome of compiling one graph. <see cref="Source"/> is null whenever <see cref="Diagnostics"/> contains an error:
/// the compiler never produces C# for a graph it could not fully understand.
/// </summary>
public sealed record GraphCompilationResult(IReadOnlyList<GraphDiagnostic> Diagnostics, IrClass? Class, GeneratedSource? Source)
{
    public bool Success => Source is not null;
}

/// <summary>
/// Compiles graph documents to C#: structural validation, type checking, lowering to IR, control-flow structuring,
/// simplification, and Roslyn-based emission. See docs/COMPILER_PIPELINE.md.
/// </summary>
public sealed class GraphCompiler(NodeLibrary library)
{
    public NodeLibrary Library { get; } = library ?? throw new ArgumentNullException(nameof(library));

    public GraphCompilationResult Compile(GraphDocument graph, GraphCompilationOptions options, string sourceDescription)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(options);

        List<GraphDiagnostic> diagnostics = [];
        IrClass? irClass = Lower(graph, options, diagnostics);

        IReadOnlyList<GraphDiagnostic> distinct = [.. diagnostics.Distinct()];
        if (irClass is null || distinct.Any(diagnostic => diagnostic.IsError))
        {
            return new GraphCompilationResult(distinct, null, null);
        }

        GeneratedSource source = CSharpEmitter.Emit(irClass, GeneratedFileName(graph), sourceDescription);
        return new GraphCompilationResult(distinct, irClass, source);
    }

    /// <summary>The generated file name for a graph: <c>{Name}.g.cs</c>.</summary>
    public static string GeneratedFileName(GraphDocument graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return graph.Name + ".g.cs";
    }

    /// <summary>True when <paramref name="name"/> is a C# identifier that is not a keyword.</summary>
    public static bool IsValidIdentifier(string name) =>
        SyntaxFacts.IsValidIdentifier(name) && SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None;

    /// <summary>True when <paramref name="name"/> is a dotted sequence of valid identifiers.</summary>
    public static bool IsValidNamespace(string name) =>
        !string.IsNullOrEmpty(name) && name.Split('.').All(IsValidIdentifier);

    private static void ValidateVariables(GraphDocument graph, GraphCompilationOptions options, List<GraphDiagnostic> diagnostics)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (GraphVariable variable in graph.Variables)
        {
            string? problem = !IsValidIdentifier(variable.Name) ? $"Variable '{variable.Name}' does not have a valid C# name."
                : !names.Add(variable.Name) ? $"More than one variable is named '{variable.Name}'."
                : TypeRef.FromName(variable.Type, options.Graphs) is not { } type ? $"Variable '{variable.Name}' has the unsupported type '{variable.Type}'."
                : LiteralValues.TryInitialValue(type, variable.Value, out string? error) is null ? $"Variable '{variable.Name}': initial value: {error}"
                : null;
            if (problem is not null)
            {
                diagnostics.Add(new(DiagnosticIds.InvalidVariable, DiagnosticSeverity.Error, problem, graph.Id));
            }
        }
    }

    private static void ValidateFunction(GraphDocument graph, GraphCompilationOptions options, List<GraphDiagnostic> diagnostics)
    {
        void Problem(string message) => diagnostics.Add(new(DiagnosticIds.InvalidFunction, DiagnosticSeverity.Error, message, graph.Id));

        if (!IsValidIdentifier(graph.Name))
        {
            Problem($"Function name '{graph.Name}' is not a valid C# method name.");
        }
        else if (graph.Name == options.HostClass || graph.Name == "Main")
        {
            Problem($"A function cannot be named '{graph.Name}'.");
        }
        else if (options.Functions.Count(function => function.Name == graph.Name) > 1)
        {
            Problem($"More than one function is named '{graph.Name}'.");
        }

        if (graph.ReturnType is not null && TypeRef.FromName(graph.ReturnType, options.Graphs) is null)
        {
            Problem($"Function '{graph.Name}' has the unsupported return type '{graph.ReturnType}'.");
        }

        var variableNames = new HashSet<string>(graph.Variables.Select(variable => variable.Name), StringComparer.Ordinal);
        var parameterNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (GraphParameter parameter in graph.Parameters)
        {
            string? problem = !IsValidIdentifier(parameter.Name) ? $"Parameter '{parameter.Name}' does not have a valid C# name."
                : !parameterNames.Add(parameter.Name) ? $"More than one parameter is named '{parameter.Name}'."
                : variableNames.Contains(parameter.Name) ? $"Parameter '{parameter.Name}' has the same name as a variable."
                : TypeRef.FromName(parameter.Type, options.Graphs) is null ? $"Parameter '{parameter.Name}' has the unsupported type '{parameter.Type}'."
                : null;
            if (problem is not null)
            {
                Problem(problem);
            }
        }
    }

    /// <summary>
    /// A class or struct: one property per graph property, with its initial value. Checks names, types, and that no type contains
    /// itself through its properties.
    /// </summary>
    private static IrClass? LowerType(GraphDocument graph, GraphCompilationOptions options, List<GraphDiagnostic> diagnostics)
    {
        string kind = graph.Kind == GraphKind.Struct ? "struct" : "class";
        void Problem(string message) => diagnostics.Add(new(DiagnosticIds.InvalidType, DiagnosticSeverity.Error, message, graph.Id));

        if (!IsValidIdentifier(graph.Name))
        {
            Problem($"'{graph.Name}' is not a valid C# {kind} name.");
        }
        else if (graph.Name == options.HostClass || options.Graphs.Count(other => other.Name == graph.Name) > 1)
        {
            // Each graph generates {Name}.g.cs, so a function of the same name would also clash.
            Problem($"Another graph is named '{graph.Name}'.");
        }

        if (graph.Nodes.Count > 0)
        {
            Problem($"A {kind} graph has properties, not nodes. Delete its {graph.Nodes.Count} node(s).");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        List<IrProperty> properties = [];
        foreach (GraphProperty property in graph.Properties)
        {
            TypeRef? type = TypeRef.FromName(property.Type, options.Graphs);
            string? error = null;
            IrExpression? initial = type is null ? null : LiteralValues.TryInitialValue(type, property.Value, out error);
            string? problem = !IsValidIdentifier(property.Name) ? $"Property '{property.Name}' does not have a valid C# name."
                : !names.Add(property.Name) ? $"More than one property is named '{property.Name}'."
                : property.Name == graph.Name ? $"Property '{property.Name}' cannot have the name of its {kind}."
                : type is null ? $"Property '{property.Name}' has the unsupported type '{property.Type}'."
                : initial is null ? $"Property '{property.Name}': initial value: {error}"
                : type.IsProjectType && Contains(type.Name, graph.Name, options.Graphs) ? $"Property '{property.Name}' makes {graph.Name} contain itself, so a {graph.Name} could never be created."
                : null;
            if (problem is not null)
            {
                Problem(problem);
            }
            else
            {
                properties.Add(new IrProperty(property.Name, type!, IsDefaultValue(initial!) ? null : initial));
            }
        }

        return new IrClass(options.RootNamespace, graph.Name, [], graph.Id,
            Kind: graph.Kind == GraphKind.Struct ? IrClassKind.Struct : IrClassKind.Class, Properties: properties);
    }

    /// <summary>True when the type named <paramref name="typeName"/> is, or has a property that contains, the type named <paramref name="target"/>.</summary>
    private static bool Contains(string typeName, string target, IReadOnlyList<GraphDocument> graphs)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([typeName]);
        while (pending.TryPop(out string? name))
        {
            if (name == target)
            {
                return true;
            }

            if (visited.Add(name) && graphs.FirstOrDefault(graph => TypeRef.IsTypeGraph(graph) && graph.Name == name) is { } type)
            {
                foreach (GraphProperty property in type.Properties)
                {
                    pending.Push(property.Type);
                }
            }
        }

        return false;
    }

    /// <summary>True for the value C# gives a property without an initializer: false, 0, or 0.0.</summary>
    private static bool IsDefaultValue(IrExpression value) =>
        value is IrLiteral { Value: false or 0 } || (value is IrLiteral { Value: double number } && number == 0 && !double.IsNegative(number));

    private IrClass? Lower(GraphDocument graph, GraphCompilationOptions options, List<GraphDiagnostic> diagnostics)
    {
        if (TypeRef.IsTypeGraph(graph))
        {
            return LowerType(graph, options, diagnostics);
        }

        bool isFunction = graph.Kind == GraphKind.Function;
        if (!isFunction && graph.Kind != GraphKind.EntryPoint)
        {
            diagnostics.Add(new(DiagnosticIds.UnsupportedGraphKind, DiagnosticSeverity.Error, $"Graph kind {graph.Kind} is not supported yet.", graph.Id));
            return null;
        }

        if (isFunction)
        {
            ValidateFunction(graph, options, diagnostics);
        }
        else if (!IsValidIdentifier(graph.Name))
        {
            diagnostics.Add(new(DiagnosticIds.InvalidIdentifier, DiagnosticSeverity.Error,
                $"Graph name '{graph.Name}' is not a valid C# type name.", graph.Id));
        }

        if (!IsValidNamespace(options.RootNamespace))
        {
            diagnostics.Add(new(DiagnosticIds.InvalidIdentifier, DiagnosticSeverity.Error,
                $"Root namespace '{options.RootNamespace}' is not a valid C# namespace.", graph.Id));
        }

        ValidateVariables(graph, options, diagnostics);
        GraphIndex index = GraphIndex.Build(graph, Library, diagnostics, options.Graphs);

        List<ResolvedNode> entries = [.. index.Nodes.Where(node => isFunction ? node.Definition is FunctionEntryNode : node.Definition is EntryNode)];
        if (isFunction)
        {
            foreach (ResolvedNode misplaced in index.Nodes.Where(node => node.Definition is EntryNode))
            {
                diagnostics.Add(new(DiagnosticIds.MultipleEntries, DiagnosticSeverity.Error,
                    "Entry belongs in the program graph. A function starts at its Function Entry node.", graph.Id, misplaced.Id));
            }
        }

        if (entries.Count == 0)
        {
            diagnostics.Add(new(DiagnosticIds.MissingEntry, DiagnosticSeverity.Error, isFunction
                ? "The function has no Function Entry node. Add one to mark where the function starts."
                : "The graph has no Entry node. Add one to mark where the program starts.", graph.Id));
            return null;
        }

        if (entries.Count > 1)
        {
            foreach (ResolvedNode extra in entries.Skip(1))
            {
                diagnostics.Add(new(DiagnosticIds.MultipleEntries, DiagnosticSeverity.Error, "A graph can have only one entry node.", graph.Id, extra.Id));
            }

            return null;
        }

        TypeRef returnType = isFunction ? TypeRef.FromName(graph.ReturnType, options.Graphs) ?? TypeRef.Void : TypeRef.Void;
        IrBlock? body = MethodLowerer.LowerBody(index, entries[0], diagnostics, returnType);
        if (body is null)
        {
            return null;
        }

        if (returnType != TypeRef.Void && !AlwaysReturns(body))
        {
            diagnostics.Add(new(DiagnosticIds.MissingReturn, DiagnosticSeverity.Error,
                $"Not every path through {graph.Name} ends at a Return Value node.", graph.Id, entries[0].Id));
        }

        IrMethod method = isFunction
            ? new IrMethod(graph.Name, returnType, MethodLowerer.Parameters(graph, options.Graphs), body, graph.Id, IsPrivate: true)
            : new IrMethod("Main", TypeRef.Void, [], body, graph.Id);
        IrSimplifier.Simplify(method);

        // Functions are methods of the program class, so the class is split over one file per graph.
        return isFunction
            ? new IrClass(options.RootNamespace, options.HostClass, [method], graph.Id, IsPartial: true)
            : new IrClass(options.RootNamespace, graph.Name, [method], graph.Id, IsPartial: options.Functions.Count > 0);
    }

    /// <summary>
    /// True when control cannot reach the end of <paramref name="block"/>: it returns on every path, or loops forever.
    /// These are the C# reachability rules for the statements the compiler generates.
    /// </summary>
    private static bool AlwaysReturns(IrBlock block) => block.Statements.Exists(statement => statement switch
    {
        IrReturn or IrThrow => true,
        IrIf branch => AlwaysReturns(branch.Then) && AlwaysReturns(branch.Else),
        IrTry attempt => (AlwaysReturns(attempt.Try) && AlwaysReturns(attempt.Catch)) || AlwaysReturns(attempt.Finally),
        IrWhile { Condition: IrLiteral { Value: true } } loop => !Breaks(loop.Body),
        _ => false,
    });

    /// <summary>True when <paramref name="body"/> contains a <c>break</c> that leaves its loop; breaks in nested loops do not count.</summary>
    private static bool Breaks(IrBlock body) => body.Statements.Exists(statement => statement switch
    {
        IrBreak => true,
        IrIf branch => Breaks(branch.Then) || Breaks(branch.Else),
        IrTry attempt => Breaks(attempt.Try) || Breaks(attempt.Catch),
        _ => false,
    });
}
