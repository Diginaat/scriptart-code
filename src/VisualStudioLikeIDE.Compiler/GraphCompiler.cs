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
/// The project's graphs. Call nodes refer to its functions, which become methods of <paramref name="HostClass"/> unless they are
/// methods of one of its classes and structs, which types name.
/// </param>
/// <param name="HostClass">The class generated from the entry graph, which also holds every function.</param>
public sealed record GraphCompilationOptions(string RootNamespace, IReadOnlyList<GraphDocument>? Graphs = null, string HostClass = "Program")
{
    public IReadOnlyList<GraphDocument> Graphs { get; init; } = Graphs ?? [];

    /// <summary>The function graphs among <see cref="Graphs"/> that are methods of <see cref="HostClass"/>, not of a class or struct.</summary>
    public IReadOnlyList<GraphDocument> Functions => [.. Graphs.Where(graph => graph.Kind == GraphKind.Function && graph.Owner is null)];
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

        GeneratedSource source = CSharpEmitter.Emit(irClass, GeneratedFileName(graph, options.Graphs), sourceDescription);
        return new GraphCompilationResult(distinct, irClass, source);
    }

    /// <summary>
    /// The generated file name for a graph: <c>{Name}.g.cs</c>, or <c>{Type}.{Name}.g.cs</c> for a method or constructor of a class
    /// or struct among <paramref name="projectGraphs"/>, since methods of different types can share a name.
    /// </summary>
    public static string GeneratedFileName(GraphDocument graph, IReadOnlyList<GraphDocument>? projectGraphs = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return graph.Owner is not null && (projectGraphs ?? []).FirstOrDefault(type => type.Id == graph.Owner && TypeRef.IsTypeGraph(type)) is { } owner
            ? $"{owner.Name}.{graph.Name}.g.cs"
            : graph.Name + ".g.cs";
    }

    /// <summary>True when <paramref name="name"/> is a C# identifier that is not a keyword.</summary>
    public static bool IsValidIdentifier(string name) =>
        SyntaxFacts.IsValidIdentifier(name) && SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None;

    /// <summary>True when <paramref name="name"/> is a dotted sequence of valid identifiers.</summary>
    public static bool IsValidNamespace(string name) =>
        !string.IsNullOrEmpty(name) && name.Split('.').All(IsValidIdentifier);

    /// <summary>
    /// The ids of the graphs among <paramref name="graphs"/> that generate async methods: those with a node that waits for an async
    /// .NET method, or that calls a function, method, or constructor graph that does (D-029). The entry graph is among them when it
    /// waits for something; it then generates <c>async Task Main()</c>.
    /// </summary>
    public IReadOnlySet<string> AsyncGraphs(IReadOnlyList<GraphDocument> graphs)
    {
        ArgumentNullException.ThrowIfNull(graphs);
        var async = new HashSet<string>(StringComparer.Ordinal);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (GraphDocument graph in graphs)
            {
                if (!async.Contains(graph.Id) && graph.Nodes.Exists(node => Waits(node, graph, graphs, async)))
                {
                    async.Add(graph.Id);
                    changed = true;
                }
            }
        }

        return async;
    }

    private bool Waits(GraphNode node, GraphDocument graph, IReadOnlyList<GraphDocument> graphs, HashSet<string> async)
    {
        if (node.Type == FunctionNodes.CallTypeId)
        {
            return node.Properties.TryGetValue(FunctionNodes.FunctionProperty, out string? called) && async.Contains(called);
        }

        if (node.Type == TypeNodes.CreateTypeId)
        {
            return node.Properties.TryGetValue(TypeNodes.ConstructorProperty, out string? constructor) && async.Contains(constructor);
        }

        return node.Type.StartsWith(ExternalApi.TypeIdPrefix, StringComparison.Ordinal)
            && Library.Resolve(node, graph, graphs) is ExternalMethodNode { Method.IsAsync: true };
    }

    /// <summary>
    /// A problem with the .NET types in <paramref name="type"/>: one that no referenced assembly offers, or, when
    /// <paramref name="createsValue"/> is true, a class without a parameterless constructor to start a variable or property with.
    /// </summary>
    private string? ExternalTypeProblem(TypeRef type, bool createsValue)
    {
        foreach (TypeRef external in type.SelfAndArguments().Where(candidate => candidate.IsExternal))
        {
            if (Library.FindExternalType(external) is not { } found)
            {
                return $"The .NET type {external.SourceName} is not offered by the project's references.";
            }

            if (createsValue && external == type && !found.HasDefaultConstructor)
            {
                return $"{external} has no parameterless constructor to start with. Use a parameter instead, or a type that has one.";
            }
        }

        return null;
    }

    private void ValidateVariables(GraphDocument graph, GraphCompilationOptions options, List<GraphDiagnostic> diagnostics)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (GraphVariable variable in graph.Variables)
        {
            TypeRef? type = TypeRef.FromName(variable.Type, options.Graphs, graph);
            string? error = null;
            string? problem = !IsValidIdentifier(variable.Name) ? $"Variable '{variable.Name}' does not have a valid C# name."
                : !names.Add(variable.Name) ? $"More than one variable is named '{variable.Name}'."
                : type is null ? $"Variable '{variable.Name}' has the unsupported type '{variable.Type}'."
                : ExternalTypeProblem(type, createsValue: true) is { } external ? $"Variable '{variable.Name}': {external}"
                : LiteralValues.TryInitialValue(type, variable.Value, out error) is null ? $"Variable '{variable.Name}': initial value: {error}"
                : null;
            if (problem is not null)
            {
                diagnostics.Add(new(DiagnosticIds.InvalidVariable, DiagnosticSeverity.Error, problem, graph.Id));
            }
        }
    }

    private void ValidateFunction(GraphDocument graph, GraphCompilationOptions options, bool isAsync, List<GraphDiagnostic> diagnostics)
    {
        void Problem(string message) => diagnostics.Add(new(DiagnosticIds.InvalidFunction, DiagnosticSeverity.Error, message, graph.Id));

        bool isConstructor = graph.Kind == GraphKind.Constructor;
        string kind = isConstructor ? "Constructor" : graph.Owner is null ? "Function" : "Method";
        GraphDocument? owner = graph.Owner is null ? null : options.Graphs.FirstOrDefault(type => type.Id == graph.Owner && TypeRef.IsTypeGraph(type));
        if (graph.Owner is not null && owner is null)
        {
            Problem($"{kind} '{graph.Name}' belongs to a class or struct that is no longer in the project.");
        }
        else if (isConstructor && owner is null)
        {
            Problem($"Constructor '{graph.Name}' does not belong to a class or struct.");
        }
        else if (!IsValidIdentifier(graph.Name))
        {
            Problem($"{(isConstructor ? "Constructor" : "Function")} name '{graph.Name}' is not a valid C# {(isConstructor ? "name" : "method name")}.");
        }
        else if (owner is not null)
        {
            // Methods and properties share the type's member names, and a member cannot have the name of its type (CS0542). A
            // constructor's name only names its file, which must not be a method's.
            if (!isConstructor && (graph.Name == owner.Name || owner.Properties.Exists(property => property.Name == graph.Name) || owner.TypeParameters.Contains(graph.Name)))
            {
                Problem($"{owner.Name} already has a member named '{graph.Name}'.");
            }
            else if (options.Graphs.Count(other => other.Kind is GraphKind.Function or GraphKind.Constructor && other.Owner == owner.Id && other.Name == graph.Name) > 1)
            {
                Problem($"More than one method or constructor of {owner.Name} is named '{graph.Name}'.");
            }
        }
        else if (graph.Name == options.HostClass || graph.Name == "Main")
        {
            Problem($"A function cannot be named '{graph.Name}'.");
        }
        else if (options.Functions.Count(function => function.Name == graph.Name) > 1)
        {
            Problem($"More than one function is named '{graph.Name}'.");
        }

        if (isConstructor && graph.ReturnType is not null)
        {
            Problem($"Constructor '{graph.Name}' cannot return a value.");
        }
        else if (graph.ReturnType is not null && TypeRef.FromName(graph.ReturnType, options.Graphs, graph) is not { } returnType)
        {
            Problem($"Function '{graph.Name}' has the unsupported return type '{graph.ReturnType}'.");
        }
        else if (graph.ReturnType is not null && ExternalTypeProblem(TypeRef.FromName(graph.ReturnType, options.Graphs, graph)!, createsValue: false) is { } external)
        {
            Problem($"Function '{graph.Name}': {external}");
        }

        if (isConstructor && isAsync)
        {
            Problem($"Constructor '{graph.Name}' waits for an async call, which a constructor cannot do. Move that work into a method.");
        }

        var variableNames = new HashSet<string>(graph.Variables.Select(variable => variable.Name), StringComparer.Ordinal);
        var parameterNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (GraphParameter parameter in graph.Parameters)
        {
            TypeRef? type = TypeRef.FromName(parameter.Type, options.Graphs, graph);
            string? problem = !IsValidIdentifier(parameter.Name) ? $"Parameter '{parameter.Name}' does not have a valid C# name."
                : !parameterNames.Add(parameter.Name) ? $"More than one parameter is named '{parameter.Name}'."
                : variableNames.Contains(parameter.Name) ? $"Parameter '{parameter.Name}' has the same name as a variable."
                : type is null ? $"Parameter '{parameter.Name}' has the unsupported type '{parameter.Type}'."
                : ExternalTypeProblem(type, createsValue: false) is { } external ? $"Parameter '{parameter.Name}': {external}"
                : null;
            if (problem is not null)
            {
                Problem(problem);
            }
        }

        // Two constructors of a type with the same parameter types are the same constructor to C# (CS0111).
        if (isConstructor && owner is not null)
        {
            string Shape(GraphDocument constructor) => string.Join(",", constructor.Parameters.Select(parameter => TypeRef.FromName(parameter.Type, options.Graphs, constructor)?.SourceName ?? parameter.Type));
            if (options.Graphs.Count(other => other.Kind == GraphKind.Constructor && other.Owner == owner.Id && Shape(other) == Shape(graph)) > 1)
            {
                Problem($"Another constructor of {owner.Name} takes the same parameter types as '{graph.Name}'.");
            }
        }
    }

    /// <summary>
    /// A class or struct: one property per graph property, with its initial value. Checks names, types, type parameters, and that no
    /// type contains itself through its properties.
    /// </summary>
    private IrClass? LowerType(GraphDocument graph, GraphCompilationOptions options, List<GraphDiagnostic> diagnostics)
    {
        bool isStruct = graph.Kind == GraphKind.Struct;
        string kind = isStruct ? "struct" : "class";
        void Problem(string message) => diagnostics.Add(new(DiagnosticIds.InvalidType, DiagnosticSeverity.Error, message, graph.Id));

        if (!IsValidIdentifier(graph.Name))
        {
            Problem($"'{graph.Name}' is not a valid C# {kind} name.");
        }
        else if (graph.Name == options.HostClass || options.Graphs.Count(other => other.Owner is null && other.Name == graph.Name) > 1)
        {
            // Each graph generates {Name}.g.cs, so a function of the same name would also clash; a method's file also names its type.
            Problem($"Another graph is named '{graph.Name}'.");
        }

        if (graph.Nodes.Count > 0)
        {
            Problem($"A {kind} graph has properties, not nodes. Delete its {graph.Nodes.Count} node(s).");
        }

        var typeParameters = new HashSet<string>(StringComparer.Ordinal);
        foreach (string typeParameter in graph.TypeParameters)
        {
            string? problem = !IsValidIdentifier(typeParameter) ? $"Type parameter '{typeParameter}' does not have a valid C# name."
                : !typeParameters.Add(typeParameter) ? $"More than one type parameter is named '{typeParameter}'."
                : typeParameter == graph.Name ? $"A type parameter cannot have the name of its {kind}."
                : options.Graphs.Any(other => TypeRef.IsTypeGraph(other) && other.Name == typeParameter) ? $"Type parameter '{typeParameter}' has the name of a class or struct."
                : null;
            if (problem is not null)
            {
                Problem(problem);
            }
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        List<IrProperty> properties = [];
        foreach (GraphProperty property in graph.Properties)
        {
            TypeRef? type = TypeRef.FromName(property.Type, options.Graphs, graph);
            string? error = null;
            IrExpression? initial = type is null ? null : LiteralValues.TryInitialValue(type, property.Value, out error);
            string? problem = !IsValidIdentifier(property.Name) ? $"Property '{property.Name}' does not have a valid C# name."
                : !names.Add(property.Name) ? $"More than one property is named '{property.Name}'."
                : property.Name == graph.Name ? $"Property '{property.Name}' cannot have the name of its {kind}."
                : typeParameters.Contains(property.Name) ? $"Property '{property.Name}' has the name of a type parameter."
                : type is null ? $"Property '{property.Name}' has the unsupported type '{property.Type}'."
                : ExternalTypeProblem(type, createsValue: true) is { } external ? $"Property '{property.Name}': {external}"
                : initial is null ? $"Property '{property.Name}': initial value: {error}"
                : type.IsProjectType && Contains(type.Name, graph.Name, options.Graphs) ? $"Property '{property.Name}' makes {graph.Name} contain itself, so a {graph.Name} could never be created."
                : null;
            if (problem is not null)
            {
                Problem(problem);
            }
            else
            {
                // A class property of a type parameter starts as default!, which the nullable analysis would otherwise warn about
                // (CS8618); a struct property needs no initializer, and one would make the struct need a constructor.
                IrExpression? initializer = initial is IrDefault ? (isStruct ? null : initial) : IsDefaultValue(initial!) ? null : initial;
                properties.Add(new IrProperty(property.Name, type!, initializer));
            }
        }

        // A type with methods or constructors is split over its own file and one file per method or constructor. Its New node runs
        // new T(), which needs a parameterless constructor written when the type's own constructors all take parameters, or when a
        // struct's property initializers must run (CS8983).
        GraphDocument[] members = [.. options.Graphs.Where(member => member.Kind is GraphKind.Function or GraphKind.Constructor && member.Owner == graph.Id)];
        GraphDocument[] constructors = [.. members.Where(member => member.Kind == GraphKind.Constructor)];
        bool needsDefaultConstructor = !constructors.Any(constructor => constructor.Parameters.Count == 0)
            && (isStruct ? properties.Any(property => property.Initializer is not null) : constructors.Length > 0);
        return new IrClass(options.RootNamespace, graph.Name, [], graph.Id, IsPartial: members.Length > 0,
            Kind: isStruct ? IrClassKind.Struct : IrClassKind.Class, Properties: properties, TypeParameters: graph.TypeParameters, DefaultConstructor: needsDefaultConstructor);
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
                // Only properties holding a class or struct directly create one; collections start empty, and a type parameter starts as its default.
                foreach (GraphProperty property in type.Properties)
                {
                    if (TypeRef.FromName(property.Type, graphs, type) is { IsProjectType: true } propertyType)
                    {
                        pending.Push(propertyType.Name);
                    }
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

        bool isConstructor = graph.Kind == GraphKind.Constructor;
        bool isFunction = graph.Kind == GraphKind.Function || isConstructor;
        if (!isFunction && graph.Kind != GraphKind.EntryPoint)
        {
            diagnostics.Add(new(DiagnosticIds.UnsupportedGraphKind, DiagnosticSeverity.Error, $"Graph kind {graph.Kind} is not supported yet.", graph.Id));
            return null;
        }

        IReadOnlySet<string> asyncGraphs = AsyncGraphs(options.Graphs.Contains(graph) ? options.Graphs : [.. options.Graphs, graph]);
        bool isAsync = asyncGraphs.Contains(graph.Id);
        if (isFunction)
        {
            ValidateFunction(graph, options, isAsync, diagnostics);
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

        TypeRef returnType = isFunction && !isConstructor ? TypeRef.FromName(graph.ReturnType, options.Graphs, graph) ?? TypeRef.Void : TypeRef.Void;
        GraphDocument? owner = isFunction && graph.Owner is not null
            ? options.Graphs.FirstOrDefault(type => type.Id == graph.Owner && TypeRef.IsTypeGraph(type))
            : null;
        IrBlock? body = MethodLowerer.LowerBody(index, entries[0], diagnostics, returnType, owner is null ? null : TypeRef.ProjectType(options.HostClass), asyncGraphs);
        if (body is null)
        {
            return null;
        }

        if (returnType != TypeRef.Void && !AlwaysReturns(body))
        {
            diagnostics.Add(new(DiagnosticIds.MissingReturn, DiagnosticSeverity.Error,
                $"Not every path through {graph.Name} ends at a Return Value node.", graph.Id, entries[0].Id));
        }

        IrMethod method = isConstructor ? new IrMethod(owner?.Name ?? graph.Name, TypeRef.Void, MethodLowerer.Parameters(graph, options.Graphs), body, graph.Id, IsStatic: false, IsConstructor: true)
            : isFunction ? new IrMethod(graph.Name, returnType, MethodLowerer.Parameters(graph, options.Graphs), body, graph.Id, IsStatic: owner is null, IsAsync: isAsync)
            : new IrMethod("Main", TypeRef.Void, [], body, graph.Id, IsAsync: isAsync);
        IrSimplifier.Simplify(method);

        // Functions are methods of the program class, and methods and constructors of a class or struct belong to it, so each class
        // is split over one file per graph.
        if (owner is not null)
        {
            return new IrClass(options.RootNamespace, owner.Name, [method], graph.Id, IsPartial: true,
                Kind: owner.Kind == GraphKind.Struct ? IrClassKind.Struct : IrClassKind.Class, TypeParameters: owner.TypeParameters);
        }

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
        IrReturn or IrThrow or IrRethrow => true,
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
