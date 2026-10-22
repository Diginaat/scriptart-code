using System.ComponentModel;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using VisualStudioLikeIDE.Compiler.Emit;
using VisualStudioLikeIDE.Compiler.Ir;
using VisualStudioLikeIDE.Compiler.Lowering;
using VisualStudioLikeIDE.Compiler.TypeSystem;

namespace VisualStudioLikeIDE.Compiler.Nodes;

/// <summary>A public .NET method that graphs can call, with every type mapped to a <see cref="TypeRef"/>.</summary>
/// <param name="DocumentationId">The method's documentation comment ID, such as <c>M:System.Math.Max(System.Int32,System.Int32)</c>. Stable across versions, so it names the node type.</param>
/// <param name="IsPure">True when calling the method has no side effects, so the node can be inlined where its result is used.</param>
/// <param name="ReturnsNullable">True when the result may be null; the call is then followed by <c>?? ""</c> or an empty list.</param>
public sealed record ExternalMethod(
    string DocumentationId,
    TypeRef DeclaringType,
    string Name,
    bool IsStatic,
    IReadOnlyList<(string Name, TypeRef Type)> Parameters,
    TypeRef ReturnType,
    bool IsPure,
    bool ReturnsNullable = false)
{
    /// <summary>The node type ID: <c>dotnet:</c> followed by the documentation ID.</summary>
    public string TypeId => ExternalApi.TypeIdPrefix + DocumentationId;

    /// <summary>For example <c>Math.Max(int, int)</c>; parameter types tell overloads apart.</summary>
    public string DisplayName => $"{DeclaringType.Name}.{Name}({string.Join(", ", Parameters.Select(parameter => parameter.Type.DisplayName))})";
}

/// <summary>
/// Finds the methods of referenced assemblies that graphs can call. A method is offered only when the generator can call it
/// exactly as written: a public, ordinary, non-generic, non-obsolete method of a public top-level type, whose parameters
/// (no ref, out, in, or params) and return type are types graphs support. Everything else is filtered out.
/// </summary>
public static class ExternalApi
{
    public const string TypeIdPrefix = "dotnet:";

    /// <summary>Framework types offered by default. Graphs reach other framework types only through these.</summary>
    public static IReadOnlyList<string> FrameworkTypes { get; } =
    [
        "System.Convert",
        "System.Environment",
        "System.IO.File",
        "System.IO.Path",
        "System.Math",
        "System.String",
        "System.Threading.Thread",
    ];

    // Pin IDs the node itself uses; a method with a parameter of this name is not offered.
    private static readonly HashSet<string> ReservedPinIds = new(["exec", "then", "instance", "result"], StringComparer.Ordinal);

    // Types whose methods neither change state nor depend on anything but their arguments.
    private static readonly HashSet<string> PureTypes = new(["System.Math", "System.String"], StringComparer.Ordinal);

    private static readonly Lazy<IReadOnlyList<ExternalMethod>> FrameworkMethods = new(() =>
        Discover([], compilation => FrameworkTypes.Select(compilation.GetTypeByMetadataName).OfType<INamedTypeSymbol>()));

    /// <summary>The callable methods of <see cref="FrameworkTypes"/>.</summary>
    public static IReadOnlyList<ExternalMethod> Framework => FrameworkMethods.Value;

    /// <summary>
    /// Every callable method of every public type in the assemblies at <paramref name="assemblyPaths"/>, such as a project's
    /// package and assembly references. Framework assemblies are always available to resolve their signatures.
    /// </summary>
    public static IReadOnlyList<ExternalMethod> FromAssemblies(IReadOnlyList<string> assemblyPaths)
    {
        ArgumentNullException.ThrowIfNull(assemblyPaths);
        return assemblyPaths.Count == 0
            ? []
            : Discover(assemblyPaths, compilation => compilation.References
                .Skip(RoslynVerifier.RuntimeReferences.Count)
                .Select(compilation.GetAssemblyOrModuleSymbol)
                .OfType<IAssemblySymbol>()
                .SelectMany(assembly => AllTypes(assembly.GlobalNamespace)));
    }

    private static List<ExternalMethod> Discover(IReadOnlyList<string> assemblyPaths, Func<CSharpCompilation, IEnumerable<INamedTypeSymbol>> types)
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            "ExternalApiDiscovery",
            references: [.. RoslynVerifier.RuntimeReferences, .. assemblyPaths.Select(path => MetadataReference.CreateFromFile(path))]);

        List<ExternalMethod> methods = [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (INamedTypeSymbol type in types(compilation))
        {
            if (type.DeclaredAccessibility != Accessibility.Public || type.ContainingType is not null || type.IsGenericType || IsHidden(type))
            {
                continue;
            }

            TypeRef declaring = MapType(type) ?? new TypeRef(type.ContainingNamespace.ToDisplayString(), type.Name);
            foreach (IMethodSymbol method in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (ToExternalMethod(method, declaring) is { } external && seen.Add(external.DocumentationId))
                {
                    methods.Add(external);
                }
            }
        }

        methods.Sort((a, b) => string.CompareOrdinal(a.DisplayName, b.DisplayName));
        return methods;
    }

    private static ExternalMethod? ToExternalMethod(IMethodSymbol method, TypeRef declaring)
    {
        if (method.DeclaredAccessibility != Accessibility.Public || method.MethodKind != MethodKind.Ordinary || method.IsGenericMethod
            || IsHidden(method) || method.GetDocumentationCommentId() is not { } id
            || (!method.IsStatic && MapType(method.ContainingType) is null))
        {
            return null;
        }

        TypeRef? returnType = method.ReturnsVoid ? TypeRef.Void : MapType(method.ReturnType);
        if (returnType is null || method.ReturnsByRef || method.ReturnsByRefReadonly)
        {
            return null;
        }

        List<(string, TypeRef)> parameters = [];
        foreach (IParameterSymbol parameter in method.Parameters)
        {
            if (parameter.RefKind != RefKind.None || parameter.IsParams || MapType(parameter.Type) is not { } type
                || !GraphCompiler.IsValidIdentifier(parameter.Name) || ReservedPinIds.Contains(parameter.Name))
            {
                return null;
            }

            parameters.Add((parameter.Name, type));
        }

        bool pure = PureTypes.Contains($"{declaring.Namespace}.{declaring.Name}") && returnType != TypeRef.Void;
        bool nullable = !method.ReturnsVoid && method.ReturnType.IsReferenceType && method.ReturnNullableAnnotation == NullableAnnotation.Annotated;
        return new ExternalMethod(id, declaring, method.Name, method.IsStatic, parameters, returnType, pure, nullable);
    }

    /// <summary>The graph type for a .NET type, or null when graphs do not support it.</summary>
    private static TypeRef? MapType(ITypeSymbol type)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean: return TypeRef.Boolean;
            case SpecialType.System_Int32: return TypeRef.Int32;
            case SpecialType.System_Double: return TypeRef.Double;
            case SpecialType.System_String: return TypeRef.String;
        }

        // Nullable annotations do not change the type; a string? result is still a string.
        return type is INamedTypeSymbol { Name: "List", Arity: 1 } list
            && list.ContainingNamespace.ToDisplayString() == "System.Collections.Generic"
            && MapType(list.TypeArguments[0]) is { ElementType: null } element && element != TypeRef.Void
            ? TypeRef.ListOf(element)
            : null;
    }

    private static bool IsHidden(ISymbol symbol) => symbol.GetAttributes().Any(attribute =>
        attribute.AttributeClass?.ToDisplayString() is "System.ObsoleteAttribute"
        || (attribute.AttributeClass?.ToDisplayString() == "System.ComponentModel.EditorBrowsableAttribute"
            && attribute.ConstructorArguments is [{ Value: int state }] && state == (int)EditorBrowsableState.Never));

    private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol root)
    {
        var pending = new Stack<INamespaceSymbol>([root]);
        while (pending.Count > 0)
        {
            INamespaceSymbol current = pending.Pop();
            foreach (INamespaceOrTypeSymbol member in current.GetMembers())
            {
                if (member is INamespaceSymbol child)
                {
                    pending.Push(child);
                }
                else if (member is INamedTypeSymbol type)
                {
                    yield return type;
                }
            }
        }
    }

    /// <summary>The node definition for <paramref name="method"/>: inlined when pure, otherwise a statement run in execution order.</summary>
    public static NodeDefinition CreateNode(ExternalMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return method.IsPure ? new ExternalPureMethodNode(method) : new ExternalMethodNode(method);
    }

    internal static IReadOnlyList<PinDefinition> DataInputs(ExternalMethod method) =>
        [
            .. method.IsStatic ? [] : new[] { PinDefinition.DataIn("instance", method.DeclaringType.DisplayName, method.DeclaringType) },
            .. method.Parameters.Select(parameter => PinDefinition.DataIn(parameter.Name, parameter.Name, parameter.Type)),
        ];

    internal static string Description(ExternalMethod method) =>
        $"Calls {method.DeclaringType.Namespace}.{method.DisplayName}{(method.ReturnType == TypeRef.Void ? "" : $" and outputs the {method.ReturnType} it returns")}.";

    internal static IrExpression Call(ExternalMethod method, ExpressionLoweringContext context)
    {
        IrExpression[] arguments = [.. method.Parameters.Select(parameter => context.Input(parameter.Name))];
        IrExpression call = method.IsStatic
            ? new IrStaticCall(method.DeclaringType, method.Name, arguments, method.ReturnType)
            : new IrInstanceCall(context.Input("instance"), method.Name, arguments, method.ReturnType);

        // Graph values are never null: a missing string becomes "" and a missing list an empty one, as Read Line does.
        if (!method.ReturnsNullable)
        {
            return call;
        }

        IrExpression fallback = method.ReturnType.IsList ? new IrObjectCreation(method.ReturnType, []) : new IrLiteral("", TypeRef.String);
        return new IrBinary(IrBinaryOperator.Coalesce, call, fallback, method.ReturnType);
    }

    internal static string ResultName(ExternalMethod method) =>
        char.ToLower(method.Name[0], CultureInfo.InvariantCulture) + method.Name[1..] is var name && GraphCompiler.IsValidIdentifier(name) ? name : "result";

    internal static string Category(ExternalMethod method) => ".NET " + method.DeclaringType.Name;
}

/// <summary>A .NET method that may have side effects: a statement node that runs once, in execution order.</summary>
public sealed class ExternalMethodNode(ExternalMethod method) : StatementNodeDefinition(
    method.TypeId,
    method.DisplayName,
    ExternalApi.Category(method),
    ExternalApi.Description(method),
    [
        PinDefinition.ExecutionIn(),
        .. ExternalApi.DataInputs(method),
        PinDefinition.ExecutionOut(),
        .. method.ReturnType == TypeRef.Void ? [] : new[] { PinDefinition.DataOut("result", "Result", method.ReturnType) },
    ])
{
    public ExternalMethod Method { get; } = method;

    public override void Lower(StatementLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IrExpression call = ExternalApi.Call(Method, context);
        if (Method.ReturnType == TypeRef.Void)
        {
            context.Emit(new IrExpressionStatement(call));
            return;
        }

        context.DeclareOutput("result", call, ExternalApi.ResultName(Method), whenUnused: call);
    }
}

/// <summary>A .NET method without side effects, such as <c>Math.Max</c>, inlined where its result is used.</summary>
public sealed class ExternalPureMethodNode(ExternalMethod method) : PureNodeDefinition(
    method.TypeId,
    method.DisplayName,
    ExternalApi.Category(method),
    ExternalApi.Description(method),
    [.. ExternalApi.DataInputs(method), PinDefinition.DataOut("result", "Result", method.ReturnType)])
{
    public ExternalMethod Method { get; } = method;

    public override IrExpression Lower(ExpressionLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ExternalApi.Call(Method, context);
    }
}
