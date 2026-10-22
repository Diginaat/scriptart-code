using System.ComponentModel;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using VisualStudioLikeIDE.Compiler.Emit;
using VisualStudioLikeIDE.Compiler.Ir;
using VisualStudioLikeIDE.Compiler.Lowering;
using VisualStudioLikeIDE.Compiler.TypeSystem;

namespace VisualStudioLikeIDE.Compiler.Nodes;

/// <summary>What a .NET member node does with the member.</summary>
public enum ExternalMemberKind
{
    Method,
    Constructor,
    GetProperty,
    SetProperty,
}

/// <summary>A .NET type that graphs can hold values of, such as <c>System.Text.StringBuilder</c>.</summary>
/// <param name="IsValueType">True for a struct, which is copied and never null.</param>
/// <param name="HasDefaultConstructor">True when <c>new T()</c> creates one, so variables and properties of the type can start with one.</param>
public sealed record ExternalType(TypeRef Type, bool IsValueType, bool HasDefaultConstructor);

/// <summary>A public .NET method, constructor, or property that graphs can use, with every type mapped to a <see cref="TypeRef"/>.</summary>
/// <param name="DocumentationId">The member's documentation comment ID, such as <c>M:System.Math.Max(System.Int32,System.Int32)</c>. Stable across versions, so it names the node type.</param>
/// <param name="Name">The method or property name; the type name for a constructor.</param>
/// <param name="Parameters">The method's or constructor's parameters; for a property setter, the one value it is set to.</param>
/// <param name="ReturnType">The result: the method's return value (its task's result for an async method), the new object, or the property value; <c>void</c> for none.</param>
/// <param name="IsPure">True when using the member has no side effects, so the node can be inlined where its result is used.</param>
/// <param name="ReturnsNullable">True when the result may be null; the call is then followed by <c>?? ""</c> or an empty collection.</param>
/// <param name="TypeParameters">The type parameters of a generic method, such as <c>TSource</c>. Its node chooses the arguments.</param>
/// <param name="IsAsync">True for a method that returns <c>Task</c> or <c>Task&lt;T&gt;</c>; its node awaits it.</param>
public sealed record ExternalMethod(
    string DocumentationId,
    TypeRef DeclaringType,
    string Name,
    bool IsStatic,
    IReadOnlyList<(string Name, TypeRef Type)> Parameters,
    TypeRef ReturnType,
    bool IsPure,
    bool ReturnsNullable = false,
    ExternalMemberKind Kind = ExternalMemberKind.Method,
    IReadOnlyList<string>? TypeParameters = null,
    bool IsAsync = false)
{
    public IReadOnlyList<string> TypeParameters { get; init; } = TypeParameters ?? [];

    /// <summary>The type arguments a generic method was instantiated with; empty for a member that is not generic or not yet instantiated.</summary>
    public IReadOnlyList<TypeRef> TypeArguments { get; init; } = [];

    /// <summary>
    /// The node type ID: <c>dotnet:</c> followed by the documentation ID, and <c>dotnet:set:</c> for a property setter, whose
    /// getter has the same documentation ID.
    /// </summary>
    public string TypeId => ExternalApi.TypeIdPrefix + (Kind == ExternalMemberKind.SetProperty ? "set:" : "") + DocumentationId;

    public bool IsGeneric => TypeParameters.Count > 0;

    /// <summary>For example <c>Math.Max(int, int)</c>; parameter types tell overloads apart.</summary>
    public string DisplayName => Kind switch
    {
        ExternalMemberKind.Constructor => $"New {DeclaringType.Name}({ParameterList})",
        ExternalMemberKind.GetProperty => $"Get {DeclaringType.Name}.{Name}",
        ExternalMemberKind.SetProperty => $"Set {DeclaringType.Name}.{Name}",
        _ => $"{DeclaringType.Name}.{Name}{(IsGeneric ? $"<{string.Join(", ", TypeArguments.Count > 0 ? TypeArguments.Select(type => type.DisplayName) : TypeParameters)}>" : "")}({ParameterList})",
    };

    private string ParameterList => string.Join(", ", Parameters.Select(parameter => parameter.Type.DisplayName));

    /// <summary>
    /// This generic method with <paramref name="typeArguments"/> for its type parameters; null when their number does not match or
    /// an argument is <c>void</c>.
    /// </summary>
    public ExternalMethod? Instantiate(IReadOnlyList<TypeRef> typeArguments)
    {
        ArgumentNullException.ThrowIfNull(typeArguments);
        if (typeArguments.Count != TypeParameters.Count || typeArguments.Contains(TypeRef.Void))
        {
            return null;
        }

        Dictionary<string, TypeRef> map = ProjectTypeDefinition.TypeArgumentMap(TypeParameters, typeArguments);
        return this with
        {
            Parameters = [.. Parameters.Select(parameter => (parameter.Name, parameter.Type.Substitute(map)))],
            ReturnType = ReturnType.Substitute(map),
            TypeArguments = typeArguments,
        };
    }
}

/// <summary>
/// Finds the members of referenced assemblies that graphs can use. A member is offered only when the generator can use it exactly
/// as written: a public, non-obsolete method, constructor, or non-indexed property of a public, non-generic, top-level type, whose
/// parameters (no ref, out, in, or params) and result are types graphs support. A generic method is offered when its type parameters
/// have no constraints beyond <c>notnull</c>. Trailing optional parameters of other types are left to their defaults. Everything
/// else is filtered out.
/// </summary>
public static class ExternalApi
{
    public const string TypeIdPrefix = "dotnet:";

    /// <summary>The node property holding the type arguments of a generic method node, such as <c>int</c>.</summary>
    public const string TypeArgumentsProperty = "typeArguments";

    /// <summary>Framework types whose static members are offered by default. Graphs reach other framework types only through these.</summary>
    public static IReadOnlyList<string> FrameworkTypes { get; } =
    [
        "System.Convert",
        "System.Environment",
        "System.IO.File",
        "System.IO.Path",
        "System.Linq.Enumerable",
        "System.Math",
        "System.String",
        "System.Threading.Thread",
        "System.Threading.Tasks.Task",
    ];

    /// <summary>
    /// Framework types that graphs can hold values of, in variables, parameters, and properties. Their constructors, properties, and
    /// instance methods are offered too.
    /// </summary>
    public static IReadOnlyList<string> FrameworkValueTypes { get; } =
    [
        "System.DateTime",
        "System.Diagnostics.Stopwatch",
        "System.Random",
        "System.Text.StringBuilder",
        "System.TimeSpan",
    ];

    // Pin IDs the node itself uses; a member with a parameter of this name is not offered.
    private static readonly HashSet<string> ReservedPinIds = new(["exec", "then", "instance", "result"], StringComparer.Ordinal);

    // Types whose members neither change state nor depend on anything but their arguments.
    private static readonly HashSet<string> PureTypes = new(["System.Math", "System.String", "System.DateTime", "System.TimeSpan"], StringComparer.Ordinal);

    // Interfaces a List<T> argument can be passed as.
    private static readonly HashSet<string> ListInterfaces = new(
        ["System.Collections.Generic.IEnumerable", "System.Collections.Generic.IReadOnlyCollection", "System.Collections.Generic.IReadOnlyList",
            "System.Collections.Generic.ICollection", "System.Collections.Generic.IList"],
        StringComparer.Ordinal);

    private static readonly Lazy<ExternalApiContents> FrameworkContents = new(() =>
        Discover([], compilation => FrameworkTypes.Concat(FrameworkValueTypes).Select(compilation.GetTypeByMetadataName).OfType<INamedTypeSymbol>()));

    /// <summary>The usable members of <see cref="FrameworkTypes"/> and <see cref="FrameworkValueTypes"/>.</summary>
    public static IReadOnlyList<ExternalMethod> Framework => FrameworkContents.Value.Members;

    /// <summary>The types of <see cref="FrameworkValueTypes"/>.</summary>
    public static IReadOnlyList<ExternalType> FrameworkTypesOfValues => FrameworkContents.Value.Types;

    /// <summary>
    /// Every usable member of every public type in the assemblies at <paramref name="assemblyPaths"/>, such as a project's package and
    /// assembly references, and the types among them that graphs can hold values of. Framework assemblies are always available to
    /// resolve their signatures.
    /// </summary>
    public static ExternalApiContents FromAssemblies(IReadOnlyList<string> assemblyPaths)
    {
        ArgumentNullException.ThrowIfNull(assemblyPaths);
        return assemblyPaths.Count == 0
            ? new ExternalApiContents([], [])
            : Discover(assemblyPaths, compilation => compilation.References
                .Skip(RoslynVerifier.RuntimeReferences.Count)
                .Select(compilation.GetAssemblyOrModuleSymbol)
                .OfType<IAssemblySymbol>()
                .SelectMany(assembly => AllTypes(assembly.GlobalNamespace)));
    }

    private static ExternalApiContents Discover(IReadOnlyList<string> assemblyPaths, Func<CSharpCompilation, IEnumerable<INamedTypeSymbol>> types)
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            "ExternalApiDiscovery",
            references: [.. RoslynVerifier.RuntimeReferences, .. assemblyPaths.Select(path => MetadataReference.CreateFromFile(path))]);

        INamedTypeSymbol[] offered = [.. types(compilation).Where(type => type.DeclaredAccessibility == Accessibility.Public
            && type.ContainingType is null && !type.IsGenericType && !IsHidden(type))];

        // A type can be held in a variable when it is a class or struct that is not static, and not the task type that async
        // methods return. Of the framework, only the listed types; the members of other assemblies may also use those.
        INamedTypeSymbol[] frameworkValues = [.. FrameworkValueTypes.Select(compilation.GetTypeByMetadataName).OfType<INamedTypeSymbol>()];
        var valueTypes = new Dictionary<INamedTypeSymbol, ExternalType>(SymbolEqualityComparer.Default);
        foreach (INamedTypeSymbol type in assemblyPaths.Count == 0 ? frameworkValues : offered.Concat(frameworkValues))
        {
            // Special types have C# keywords (int, string, object, …), except DateTime, which is an ordinary value type for graphs.
            if (type.IsStatic || type.TypeKind is not (TypeKind.Class or TypeKind.Struct) || type.IsGenericType
                || type.SpecialType is not (SpecialType.None or SpecialType.System_DateTime)
                || FullName(type) == "System.Threading.Tasks.Task")
            {
                continue;
            }

            bool hasDefault = type.IsValueType || (!type.IsAbstract && type.InstanceConstructors.Any(constructor =>
                constructor.DeclaredAccessibility == Accessibility.Public && constructor.Parameters.Length == 0 && !IsHidden(constructor)));
            valueTypes.TryAdd(type, new ExternalType(TypeRef.External(type.ContainingNamespace.ToDisplayString(), type.Name), type.IsValueType, hasDefault));
        }

        var mapper = new TypeMapper(valueTypes);
        List<ExternalMethod> members = [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (INamedTypeSymbol type in offered)
        {
            TypeRef declaring = mapper.Map(type, isParameter: false) ?? TypeRef.External(type.ContainingNamespace.ToDisplayString(), type.Name);
            foreach (ISymbol member in type.GetMembers())
            {
                foreach (ExternalMethod external in ToExternalMembers(member, declaring, mapper))
                {
                    if (seen.Add(external.TypeId))
                    {
                        members.Add(external);
                    }
                }
            }
        }

        members = RemoveShadowedOverloads(members);
        members.Sort((a, b) => string.CompareOrdinal(a.DisplayName, b.DisplayName));
        return new ExternalApiContents(members, [.. valueTypes.Values.OrderBy(type => type.Type.SourceName, StringComparer.Ordinal)]);
    }

    /// <summary>
    /// A call that leaves out optional parameters can bind to another overload with exactly the remaining parameters, so a member that
    /// dropped parameters is not offered when such an overload is.
    /// </summary>
    private static List<ExternalMethod> RemoveShadowedOverloads(List<ExternalMethod> members)
    {
        static string Shape(ExternalMethod member) =>
            $"{member.Kind}|{member.DeclaringType.SourceName}|{member.Name}|{member.IsStatic}|{member.TypeParameters.Count}|{string.Join(",", member.Parameters.Select(parameter => parameter.Type.SourceName))}";

        // Members that keep all their parameters list them all in their documentation ID; those come first.
        return [.. members
            .GroupBy(Shape)
            .Select(group => group.OrderBy(member => member.DocumentationId.Count(character => character == ',')).First())];
    }

    private static IEnumerable<ExternalMethod> ToExternalMembers(ISymbol member, TypeRef declaring, TypeMapper mapper)
    {
        if (member.DeclaredAccessibility != Accessibility.Public || IsHidden(member) || member.GetDocumentationCommentId() is not { } id)
        {
            yield break;
        }

        // Instance members are offered only on types graphs have values of.
        bool hasValues = declaring.Keyword is not null || mapper.IsValueType(member.ContainingType);
        bool pure = PureTypes.Contains($"{declaring.Namespace}.{declaring.Name}");
        switch (member)
        {
            case IMethodSymbol { MethodKind: MethodKind.Ordinary } method when method.IsStatic || hasValues:
                if (ToMethod(method, id, declaring, mapper, pure) is { } ordinary)
                {
                    yield return ordinary;
                }

                break;
            case IMethodSymbol { MethodKind: MethodKind.Constructor, IsStatic: false } constructor when hasValues && !constructor.ContainingType.IsAbstract:
                if (Parameters(constructor, mapper) is { } parameters)
                {
                    // A class object has identity, so creating one is a statement; a struct value is pure, as for the project's types.
                    yield return new ExternalMethod(id, declaring, declaring.Name, IsStatic: true, parameters, declaring, IsPure: constructor.ContainingType.IsValueType, Kind: ExternalMemberKind.Constructor);
                }

                break;
            case IPropertySymbol { IsIndexer: false } property when (property.IsStatic || hasValues) && property.RefKind == RefKind.None:
                if (mapper.Map(property.Type, isParameter: false) is not { } type || IsNullableResult(property.Type, property.NullableAnnotation, type))
                {
                    break;
                }

                if (property.GetMethod is { DeclaredAccessibility: Accessibility.Public })
                {
                    // Reading a property happens where its value is used, like Get of a property of the project's types.
                    yield return new ExternalMethod(id, declaring, property.Name, property.IsStatic, [], type, IsPure: true,
                        ReturnsNullable: property.NullableAnnotation == NullableAnnotation.Annotated, Kind: ExternalMemberKind.GetProperty);
                }

                // Setting a property of a struct value would change a copy (CS1612), as with the project's structs.
                if (property.SetMethod is { DeclaredAccessibility: Accessibility.Public, IsInitOnly: false } && (property.IsStatic || !property.ContainingType.IsValueType))
                {
                    yield return new ExternalMethod(id, declaring, property.Name, property.IsStatic, [("value", mapper.Map(property.Type, isParameter: true)!)], TypeRef.Void,
                        IsPure: false, Kind: ExternalMemberKind.SetProperty);
                }

                break;
        }
    }

    private static ExternalMethod? ToMethod(IMethodSymbol method, string id, TypeRef declaring, TypeMapper mapper, bool pure)
    {
        if (method.IsGenericMethod && method.TypeParameters.Any(parameter =>
            parameter.HasReferenceTypeConstraint || parameter.HasValueTypeConstraint || parameter.HasUnmanagedTypeConstraint
            || parameter.HasConstructorConstraint || parameter.ConstraintTypes.Length > 0 || parameter.AllowsRefLikeType))
        {
            return null;
        }

        if (method.ReturnsByRef || method.ReturnsByRefReadonly || Parameters(method, mapper) is not { } parameters)
        {
            return null;
        }

        // Task and Task<T> are awaited: the node's result is the task's result.
        ITypeSymbol returned = method.ReturnType;
        NullableAnnotation annotation = method.ReturnNullableAnnotation;
        bool isAsync = method.ReturnType is INamedTypeSymbol { Name: "Task", Arity: <= 1 } task && task.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks";
        bool returnsNothing = method.ReturnsVoid || (isAsync && method.ReturnType is INamedTypeSymbol { Arity: 0 });
        if (isAsync && !returnsNothing)
        {
            var result = (INamedTypeSymbol)method.ReturnType;
            returned = result.TypeArguments[0];
            annotation = result.TypeArgumentNullableAnnotations[0];
        }

        TypeRef? returnType = returnsNothing ? TypeRef.Void : mapper.Map(returned, isParameter: false);
        if (returnType is null || (returnType != TypeRef.Void && IsNullableResult(returned, annotation, returnType)))
        {
            return null;
        }

        bool nullable = returnType != TypeRef.Void && returned.IsReferenceType && annotation == NullableAnnotation.Annotated;
        return new ExternalMethod(id, declaring, method.Name, method.IsStatic, parameters, returnType, pure && returnType != TypeRef.Void && !isAsync, nullable,
            TypeParameters: [.. method.TypeParameters.Select(parameter => parameter.Name)], IsAsync: isAsync);
    }

    /// <summary>
    /// True when a result may be null and there is no empty value to put in its place: graph values are never null, so a string or
    /// collection is replaced by an empty one, and members returning any other nullable type are not offered.
    /// </summary>
    private static bool IsNullableResult(ITypeSymbol type, NullableAnnotation annotation, TypeRef mapped) =>
        (annotation == NullableAnnotation.Annotated && (type.IsReferenceType || type is ITypeParameterSymbol) && mapped != TypeRef.String && !mapped.IsList && !mapped.IsDictionary)
        || type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

    /// <summary>
    /// The parameters graphs can pass, or null when one of them cannot be passed. Optional parameters from the first one that cannot
    /// be passed on are left out, so the method uses their default values.
    /// </summary>
    private static List<(string, TypeRef)>? Parameters(IMethodSymbol method, TypeMapper mapper)
    {
        List<(string, TypeRef)> parameters = [];
        foreach (IParameterSymbol parameter in method.Parameters)
        {
            TypeRef? type = parameter.RefKind == RefKind.None && !parameter.IsParams ? mapper.Map(parameter.Type, isParameter: true) : null;
            if (type is null && parameter.IsOptional)
            {
                break;
            }

            if (type is null || !GraphCompiler.IsValidIdentifier(parameter.Name) || ReservedPinIds.Contains(parameter.Name))
            {
                return null;
            }

            parameters.Add((parameter.Name, type));
        }

        return parameters;
    }

    private static string FullName(INamedTypeSymbol type) => $"{type.ContainingNamespace.ToDisplayString()}.{type.Name}";

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

    /// <summary>The Type Arguments property of a generic method's node; none for other members.</summary>
    internal static IReadOnlyList<PropertyDefinition> Properties(ExternalMethod method) => method.IsGeneric
        ? [new PropertyDefinition(TypeArgumentsProperty, "Type Arguments", TypeRef.String, DefaultTypeArguments(method))]
        : [];

    /// <summary><c>int</c> for each type parameter: what a generic method's node starts with.</summary>
    internal static string DefaultTypeArguments(ExternalMethod method) => string.Join(", ", method.TypeParameters.Select(_ => "int"));

    internal static string Description(ExternalMethod method) => method.Kind switch
    {
        ExternalMemberKind.Constructor => $"Creates a new {method.DeclaringType.Namespace}.{method.DeclaringType.Name}.",
        ExternalMemberKind.GetProperty => $"The {method.ReturnType} property {method.Name} of {method.DeclaringType.Namespace}.{method.DeclaringType.Name}.",
        ExternalMemberKind.SetProperty => $"Sets the property {method.Name} of {method.DeclaringType.Namespace}.{method.DeclaringType.Name}.",
        _ => $"Calls {method.DeclaringType.Namespace}.{method.DisplayName}{(method.ReturnType == TypeRef.Void ? "" : $" and outputs the {method.ReturnType} it returns")}"
            + (method.IsAsync ? ", waiting for it to finish." : ".")
            + (method.IsGeneric ? " Choose the types it works with in Type Arguments." : ""),
    };

    internal static IrExpression Call(ExternalMethod method, ExpressionLoweringContext context)
    {
        IrExpression[] arguments = [.. method.Parameters.Select(parameter => context.Input(parameter.Name))];
        IrExpression receiver = method.IsStatic ? new IrTypeReference(method.DeclaringType) : context.Input("instance");
        IrExpression call = method.Kind switch
        {
            ExternalMemberKind.Constructor => new IrObjectCreation(method.DeclaringType, arguments),
            ExternalMemberKind.GetProperty => new IrMemberAccess(receiver, method.Name, method.ReturnType),
            _ when method.IsStatic => new IrStaticCall(method.DeclaringType, method.Name, arguments, method.ReturnType, method.TypeArguments),
            _ => new IrInstanceCall(receiver, method.Name, arguments, method.ReturnType, method.TypeArguments),
        };

        if (method.IsAsync)
        {
            call = new IrAwait(call, method.ReturnType);
        }

        // Graph values are never null: a missing string becomes "" and a missing collection an empty one, as Read Line does.
        if (!method.ReturnsNullable)
        {
            return call;
        }

        IrExpression fallback = method.ReturnType.IsList || method.ReturnType.IsDictionary ? new IrObjectCreation(method.ReturnType, []) : new IrLiteral("", TypeRef.String);
        return new IrBinary(IrBinaryOperator.Coalesce, call, fallback, method.ReturnType);
    }

    internal static string ResultName(ExternalMethod method)
    {
        string source = method.Kind == ExternalMemberKind.Constructor ? method.DeclaringType.Name : method.Name;
        string name = char.ToLower(source[0], CultureInfo.InvariantCulture) + source[1..];
        return GraphCompiler.IsValidIdentifier(name) ? name : "result";
    }

    internal static string Category(ExternalMethod method) => ".NET " + method.DeclaringType.Name;

    /// <summary>Maps .NET types to graph types: the simple types, lists, dictionaries, the value types found, and a generic method's type parameters.</summary>
    private sealed class TypeMapper(Dictionary<INamedTypeSymbol, ExternalType> valueTypes)
    {
        public bool IsValueType(INamedTypeSymbol type) => valueTypes.ContainsKey(type);

        /// <summary>The graph type for a .NET type, or null when graphs do not support it. A parameter also accepts a list as an interface it implements.</summary>
        public TypeRef? Map(ITypeSymbol type, bool isParameter)
        {
            switch (type.SpecialType)
            {
                case SpecialType.System_Boolean: return TypeRef.Boolean;
                case SpecialType.System_Int32: return TypeRef.Int32;
                case SpecialType.System_Double: return TypeRef.Double;
                case SpecialType.System_String: return TypeRef.String;
            }

            if (type is ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Method } parameter)
            {
                return TypeRef.TypeParameter(parameter.Name);
            }

            if (type is not INamedTypeSymbol named)
            {
                return null;
            }

            if (valueTypes.TryGetValue(named, out ExternalType? external))
            {
                return external.Type;
            }

            // Nullable annotations do not change the type; a string? result is still a string.
            string name = $"{named.ContainingNamespace?.ToDisplayString()}.{named.Name}";
            TypeRef?[] arguments = [.. named.TypeArguments.Select(argument => Map(argument, isParameter: false))];
            if (arguments.Contains(null))
            {
                return null;
            }

            return (name, arguments.Length) switch
            {
                ("System.Collections.Generic.List", 1) => TypeRef.ListOf(arguments[0]!),
                ("System.Collections.Generic.Dictionary", 2) when TypeRef.KeyTypes.Contains(arguments[0]!) => TypeRef.DictionaryOf(arguments[0]!, arguments[1]!),
                (_, 1) when isParameter && ListInterfaces.Contains(name) => TypeRef.ListOf(arguments[0]!),
                _ => null,
            };
        }
    }
}

/// <summary>What <see cref="ExternalApi.FromAssemblies"/> found: the members graphs can use and the types graphs can hold values of.</summary>
public sealed record ExternalApiContents(IReadOnlyList<ExternalMethod> Members, IReadOnlyList<ExternalType> Types);

/// <summary>A .NET member that may have side effects: a statement node that runs once, in execution order.</summary>
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
    ],
    ExternalApi.Properties(method))
{
    public ExternalMethod Method { get; } = method;

    public override IReadOnlyDictionary<string, string> InitialProperties { get; init; } = method.IsGeneric
        ? new Dictionary<string, string> { [ExternalApi.TypeArgumentsProperty] = method.TypeArguments.Count > 0 ? TypeRef.ArgumentNames(method.TypeArguments) : ExternalApi.DefaultTypeArguments(method) }
        : new Dictionary<string, string>();

    public override string? ValidateProperty(PropertyDefinition definition, string value) =>
        definition?.Id == ExternalApi.TypeArgumentsProperty ? null : base.ValidateProperty(definition!, value);

    public override void Lower(StatementLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Method.Kind == ExternalMemberKind.SetProperty)
        {
            IrExpression receiver = Method.IsStatic ? new IrTypeReference(Method.DeclaringType) : context.Input("instance");
            context.Emit(new IrMemberAssignment(receiver, Method.Name, context.Input("value")));
            return;
        }

        IrExpression call = ExternalApi.Call(Method, context);
        if (Method.ReturnType == TypeRef.Void)
        {
            context.Emit(new IrExpressionStatement(call));
            return;
        }

        context.DeclareOutput("result", call, ExternalApi.ResultName(Method), whenUnused: call);
    }
}

/// <summary>A .NET member without side effects, such as <c>Math.Max</c> or a property read, inlined where its result is used.</summary>
public sealed class ExternalPureMethodNode(ExternalMethod method) : PureNodeDefinition(
    method.TypeId,
    method.DisplayName,
    ExternalApi.Category(method),
    ExternalApi.Description(method),
    [.. ExternalApi.DataInputs(method), PinDefinition.DataOut("result", "Result", method.ReturnType)],
    ExternalApi.Properties(method))
{
    public ExternalMethod Method { get; } = method;

    public override IReadOnlyDictionary<string, string> InitialProperties { get; init; } = method.IsGeneric
        ? new Dictionary<string, string> { [ExternalApi.TypeArgumentsProperty] = method.TypeArguments.Count > 0 ? TypeRef.ArgumentNames(method.TypeArguments) : ExternalApi.DefaultTypeArguments(method) }
        : new Dictionary<string, string>();

    public override string? ValidateProperty(PropertyDefinition definition, string value) =>
        definition?.Id == ExternalApi.TypeArgumentsProperty ? null : base.ValidateProperty(definition!, value);

    public override IrExpression Lower(ExpressionLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ExternalApi.Call(Method, context);
    }
}
