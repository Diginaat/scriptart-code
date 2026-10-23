using System.ComponentModel;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ScriptArt.Compiler.Emit;
using ScriptArt.Compiler.Ir;
using ScriptArt.Compiler.Lowering;
using ScriptArt.Compiler.TypeSystem;

namespace ScriptArt.Compiler.Nodes;

/// <summary>What a .NET member node does with the member.</summary>
public enum ExternalMemberKind
{
    Method,
    Constructor,
    GetProperty,
    SetProperty,

    /// <summary>An arithmetic, comparison, or negation operator a type defines, such as Godot's <c>Vector2 + Vector2</c>; written as the operator.</summary>
    Operator,

    /// <summary>
    /// An event, such as Godot's <c>BaseButton.Pressed</c> signal; its node subscribes a handler. <see cref="ExternalMethod.Parameters"/>
    /// are the values the handler receives.
    /// </summary>
    Event,
}

/// <summary>A .NET type that graphs can hold values of, such as <c>System.Text.StringBuilder</c>.</summary>
/// <param name="IsValueType">True for a struct, which is copied and never null.</param>
/// <param name="HasDefaultConstructor">True when <c>new T()</c> creates one, so variables and properties of the type can start with one.</param>
public sealed record ExternalType(TypeRef Type, bool IsValueType, bool HasDefaultConstructor)
{
    /// <summary>The public classes this class derives from, nearest first, without <c>object</c>. A value of it connects to inputs of each.</summary>
    public IReadOnlyList<TypeRef> BaseTypes { get; init; } = [];

    /// <summary>True for a class that classes of the project can derive from: not sealed, and with a constructor they can call.</summary>
    public bool IsInheritable { get; init; }

    /// <summary>The public virtual or abstract instance methods declared on this type, which a derived class of the project can override.</summary>
    public IReadOnlyList<ExternalMethod> Overridable { get; init; } = [];
}

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
    /// The parameters that are <c>float</c> in .NET; graphs pass a <c>double</c>, converted at the call (D-048). Godot's API uses
    /// <c>float</c> for most numbers.
    /// </summary>
    public IReadOnlyList<string> FloatParameters { get; init; } = [];

    /// <summary>True when the .NET result is a <c>float</c>, which graphs read as a <c>double</c>.</summary>
    public bool ReturnsFloat { get; init; }

    /// <summary>
    /// The type parameters constrained to classes (<c>where T : class</c>), such as Godot's <c>GetNode&lt;T&gt;</c>; their type
    /// arguments must be classes.
    /// </summary>
    public IReadOnlyList<string> ClassTypeParameters { get; init; } = [];

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
        ExternalMemberKind.Event => $"On {DeclaringType.Name}.{Name}",
        ExternalMemberKind.Operator when ExternalApi.Operators[Name] is var (_, _, symbol) => Parameters.Count == 1
            ? $"{symbol}{Parameters[0].Type.DisplayName}"
            : $"{Parameters[0].Type.DisplayName} {symbol} {Parameters[1].Type.DisplayName}",
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
/// as written: a public, non-obsolete method, constructor, non-indexed property, or field of a public, non-generic, top-level type, whose
/// parameters (no ref, out, in, or params) and result are types graphs support. A generic method is offered when its type parameters
/// have no constraints beyond <c>notnull</c> and <c>class</c>. Trailing optional parameters of other types are left to their defaults. Everything
/// else is filtered out. Two mappings make Godot's API usable (D-048): a <c>float</c> is read and passed as a <c>double</c>, and a type
/// that converts implicitly to and from <c>string</c>, such as <c>StringName</c>, is a <c>string</c>.
/// </summary>
public static class ExternalApi
{
    public const string TypeIdPrefix = "dotnet:";

    /// <summary>The user-defined operators offered as nodes (D-049), by metadata name: the IR operator, or Negate for unary minus.</summary>
    internal static readonly IReadOnlyDictionary<string, (IrBinaryOperator? Binary, IrUnaryOperator? Unary, string Symbol)> Operators =
        new Dictionary<string, (IrBinaryOperator?, IrUnaryOperator?, string)>(StringComparer.Ordinal)
        {
            ["op_Addition"] = (IrBinaryOperator.Add, null, "+"),
            ["op_Subtraction"] = (IrBinaryOperator.Subtract, null, "-"),
            ["op_Multiply"] = (IrBinaryOperator.Multiply, null, "*"),
            ["op_Division"] = (IrBinaryOperator.Divide, null, "/"),
            ["op_Modulus"] = (IrBinaryOperator.Remainder, null, "%"),
            ["op_Equality"] = (IrBinaryOperator.Equal, null, "=="),
            ["op_Inequality"] = (IrBinaryOperator.NotEqual, null, "!="),
            ["op_LessThan"] = (IrBinaryOperator.LessThan, null, "<"),
            ["op_LessThanOrEqual"] = (IrBinaryOperator.LessThanOrEqual, null, "<="),
            ["op_GreaterThan"] = (IrBinaryOperator.GreaterThan, null, ">"),
            ["op_GreaterThanOrEqual"] = (IrBinaryOperator.GreaterThanOrEqual, null, ">="),
            ["op_UnaryNegation"] = (null, IrUnaryOperator.Negate, "-"),
        };

    /// <summary><c>float</c>: not a graph type, but the type .NET members take where graphs pass a <c>double</c>.</summary>
    internal static readonly TypeRef Float = new("System", "Single", "float");

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
            references: [.. RoslynVerifier.RuntimeReferences, .. assemblyPaths.Select(RoslynVerifier.FileReference)]);

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
            bool inheritable = type.TypeKind == TypeKind.Class && !type.IsSealed && type.InstanceConstructors.Any(constructor =>
                constructor.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal && constructor.Parameters.Length == 0);
            valueTypes.TryAdd(type, new ExternalType(TypeRef.External(type.ContainingNamespace.ToDisplayString(), type.Name), type.IsValueType, hasDefault)
            {
                BaseTypes = [.. BaseClasses(type).Select(baseType => TypeRef.External(baseType.ContainingNamespace.ToDisplayString(), baseType.Name))],
                IsInheritable = inheritable,
            });
        }

        var mapper = new TypeMapper(valueTypes);
        foreach ((INamedTypeSymbol type, ExternalType external) in valueTypes.ToArray())
        {
            if (external.IsInheritable)
            {
                valueTypes[type] = external with { Overridable = [.. OverridableMethods(type, external.Type, mapper)] };
            }
        }

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
    /// dropped parameters is not offered when such an overload is. Likewise a float overload is not offered beside the double one
    /// that graphs see with the same types.
    /// </summary>
    private static List<ExternalMethod> RemoveShadowedOverloads(List<ExternalMethod> members)
    {
        static string Shape(ExternalMethod member) =>
            $"{member.Kind}|{member.DeclaringType.SourceName}|{member.Name}|{member.IsStatic}|{member.TypeParameters.Count}|{string.Join(",", member.Parameters.Select(parameter => parameter.Type.SourceName))}";

        // Members that take and return the types graphs have come first, so a float overload never hides the double one (D-048);
        // then members that keep all their parameters, which list them all in their documentation ID.
        return [.. members
            .GroupBy(Shape)
            .Select(group => group
                .OrderBy(member => member.FloatParameters.Count + (member.ReturnsFloat ? 1 : 0))
                .ThenBy(member => member.DocumentationId.Count(character => character == ','))
                .First())];
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
            case IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator } op when Operators.ContainsKey(op.Name):
                // Operators have no side effects to graphs, like the built-in math nodes.
                if (ToMethod(op, id, declaring, mapper, pure: true) is { } operatorMethod && operatorMethod.ReturnType != TypeRef.Void)
                {
                    yield return operatorMethod with { Kind = ExternalMemberKind.Operator, IsPure = true };
                }

                break;
            case IMethodSymbol { MethodKind: MethodKind.Constructor, IsStatic: false } constructor when hasValues && !constructor.ContainingType.IsAbstract:
                if (Parameters(constructor, mapper) is { } parameters)
                {
                    // A class object has identity, so creating one is a statement; a struct value is pure, as for the project's types.
                    yield return new ExternalMethod(id, declaring, declaring.Name, IsStatic: true, parameters, declaring, IsPure: constructor.ContainingType.IsValueType, Kind: ExternalMemberKind.Constructor)
                    {
                        FloatParameters = FloatParametersOf(constructor, parameters.Count),
                    };
                }

                break;
            case IEventSymbol { DeclaredAccessibility: Accessibility.Public, Type: INamedTypeSymbol { DelegateInvokeMethod: { ReturnsVoid: true } invoke } } ev
                when (ev.IsStatic || hasValues) && !IsHidden(ev):
                // The handler's parameters are its outputs; a lambda takes their types from the event, so float and StringName work.
                if (Parameters(invoke, mapper) is { } received && received.Count == invoke.Parameters.Length
                    && !received.Exists(parameter => parameter.Item1 == "handler"))
                {
                    yield return new ExternalMethod(id, declaring, ev.Name, ev.IsStatic, received, TypeRef.Void, IsPure: false, Kind: ExternalMemberKind.Event);
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
                        ReturnsNullable: property.NullableAnnotation == NullableAnnotation.Annotated, Kind: ExternalMemberKind.GetProperty)
                    {
                        ReturnsFloat = property.Type.SpecialType == SpecialType.System_Single,
                    };
                }

                // Setting a property of a struct value would change a copy (CS1612), as with the project's structs.
                if (property.SetMethod is { DeclaredAccessibility: Accessibility.Public, IsInitOnly: false } && (property.IsStatic || !property.ContainingType.IsValueType))
                {
                    yield return new ExternalMethod(id, declaring, property.Name, property.IsStatic, [("value", mapper.Map(property.Type, isParameter: true)!)], TypeRef.Void,
                        IsPure: false, Kind: ExternalMemberKind.SetProperty)
                    {
                        FloatParameters = property.Type.SpecialType == SpecialType.System_Single ? ["value"] : [],
                    };
                }

                break;
            case IFieldSymbol { IsConst: false, IsImplicitlyDeclared: false } field when field.IsStatic || hasValues:
                // A public field reads and writes like a property, such as Godot's Vector2.X; a readonly one only reads.
                if (mapper.Map(field.Type, isParameter: false) is not { } fieldType || IsNullableResult(field.Type, field.NullableAnnotation, fieldType))
                {
                    break;
                }

                yield return new ExternalMethod(id, declaring, field.Name, field.IsStatic, [], fieldType, IsPure: true,
                    ReturnsNullable: field.NullableAnnotation == NullableAnnotation.Annotated, Kind: ExternalMemberKind.GetProperty)
                {
                    ReturnsFloat = field.Type.SpecialType == SpecialType.System_Single,
                };
                if (!field.IsReadOnly && (field.IsStatic || !field.ContainingType.IsValueType))
                {
                    yield return new ExternalMethod(id, declaring, field.Name, field.IsStatic, [("value", mapper.Map(field.Type, isParameter: true)!)], TypeRef.Void,
                        IsPure: false, Kind: ExternalMemberKind.SetProperty)
                    {
                        FloatParameters = field.Type.SpecialType == SpecialType.System_Single ? ["value"] : [],
                    };
                }

                break;
        }
    }

    // The public classes a type derives from, nearest first; object is left out, as every class derives from it.
    private static IEnumerable<INamedTypeSymbol> BaseClasses(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? baseType = type.BaseType; baseType is { SpecialType: not SpecialType.System_Object }; baseType = baseType.BaseType)
        {
            if (baseType.DeclaredAccessibility != Accessibility.Public || baseType.IsGenericType)
            {
                yield break;
            }

            yield return baseType;
        }
    }

    // The public virtual and abstract instance methods declared on the type whose signatures graphs support.
    private static IEnumerable<ExternalMethod> OverridableMethods(INamedTypeSymbol type, TypeRef declaring, TypeMapper mapper) =>
        type.GetMembers().OfType<IMethodSymbol>()
            .Where(method => method is { MethodKind: MethodKind.Ordinary, IsStatic: false, IsSealed: false, IsGenericMethod: false, DeclaredAccessibility: Accessibility.Public }
                && (method.IsVirtual || method.IsAbstract || method.IsOverride) && method.GetDocumentationCommentId() is not null)
            .Select(method => ToMethod(method, method.GetDocumentationCommentId()!, declaring, mapper, pure: false))
            .OfType<ExternalMethod>()
            .Where(method => !method.IsAsync && method.FloatParameters.Count == 0 && !method.ReturnsFloat);

    private static ExternalMethod? ToMethod(IMethodSymbol method, string id, TypeRef declaring, TypeMapper mapper, bool pure)
    {
        // Only the class constraint is checked by the node library (NodeLibrary.Resolve); methods with other constraints are not offered.
        if (method.IsGenericMethod && method.TypeParameters.Any(parameter =>
            parameter.HasValueTypeConstraint || parameter.HasUnmanagedTypeConstraint
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
            TypeParameters: [.. method.TypeParameters.Select(parameter => parameter.Name)], IsAsync: isAsync)
        {
            FloatParameters = FloatParametersOf(method, parameters.Count),
            ReturnsFloat = returned.SpecialType == SpecialType.System_Single,
            ClassTypeParameters = [.. method.TypeParameters.Where(parameter => parameter.HasReferenceTypeConstraint).Select(parameter => parameter.Name)],
        };
    }

    // The float parameters among the first count, which are those graphs pass.
    private static string[] FloatParametersOf(IMethodSymbol method, int count) =>
        [.. method.Parameters.Take(count).Where(parameter => parameter.Type.SpecialType == SpecialType.System_Single).Select(parameter => parameter.Name)];

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

            // A parameter named with a keyword, such as Godot's @event, is fine: its name is only a pin id, and arguments are positional.
            if (type is null || !SyntaxFacts.IsValidIdentifier(parameter.Name) || ReservedPinIds.Contains(parameter.Name))
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
        return method.Kind == ExternalMemberKind.Event ? new ExternalEventNode(method)
            : method.IsPure ? new ExternalPureMethodNode(method) : new ExternalMethodNode(method);
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
        ExternalMemberKind.Event => $"Runs Handler each time {method.DeclaringType.Namespace}.{method.DeclaringType.Name}.{method.Name} happens (for Godot, each time the signal is emitted), from then on. Generates {method.Name} += (…) => {{ … }}.",
        ExternalMemberKind.Operator => $"{method.DisplayName}, as {method.DeclaringType.Namespace}.{method.DeclaringType.Name} defines it, giving a {method.ReturnType}.",
        _ => $"Calls {method.DeclaringType.Namespace}.{method.DisplayName}{(method.ReturnType == TypeRef.Void ? "" : $" and outputs the {method.ReturnType} it returns")}"
            + (method.IsAsync ? ", waiting for it to finish." : ".")
            + (method.IsGeneric ? " Choose the types it works with in Type Arguments." : ""),
    };

    internal static IrExpression Call(ExternalMethod method, ExpressionLoweringContext context)
    {
        IrExpression[] arguments = [.. method.Parameters.Select(parameter => Argument(method, parameter.Name, context.Input(parameter.Name)))];
        IrExpression receiver = method.IsStatic ? new IrTypeReference(method.DeclaringType) : context.Input("instance");
        IrExpression call = method.Kind switch
        {
            ExternalMemberKind.Constructor => new IrObjectCreation(method.DeclaringType, arguments),
            ExternalMemberKind.GetProperty => new IrMemberAccess(receiver, method.Name, method.ReturnType),
            ExternalMemberKind.Operator when Operators[method.Name] is { Binary: { } binary } => new IrBinary(binary, arguments[0], arguments[1], method.ReturnType),
            ExternalMemberKind.Operator => new IrUnary(Operators[method.Name].Unary!.Value, arguments[0], method.ReturnType),
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

    /// <summary>The value graphs pass for a parameter: a <c>double</c> is narrowed to the <c>float</c> the member takes.</summary>
    internal static IrExpression Argument(ExternalMethod method, string parameter, IrExpression value) =>
        method.FloatParameters.Contains(parameter) ? new IrConvert(value, Float) : value;

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

        private static bool ConvertsToAndFromString(INamedTypeSymbol type)
        {
            IMethodSymbol[] conversions = [.. type.GetMembers(WellKnownMemberNames.ImplicitConversionName).OfType<IMethodSymbol>()
                .Where(conversion => conversion.DeclaredAccessibility == Accessibility.Public)];
            return conversions.Any(conversion => conversion.Parameters is [{ Type.SpecialType: SpecialType.System_String }])
                && conversions.Any(conversion => conversion.ReturnType.SpecialType == SpecialType.System_String);
        }

        /// <summary>The graph type for a .NET type, or null when graphs do not support it. A parameter also accepts a list as an interface it implements.</summary>
        public TypeRef? Map(ITypeSymbol type, bool isParameter)
        {
            switch (type.SpecialType)
            {
                case SpecialType.System_Boolean: return TypeRef.Boolean;
                case SpecialType.System_Int32: return TypeRef.Int32;
                case SpecialType.System_Double: return TypeRef.Double;
                case SpecialType.System_String: return TypeRef.String;

                // Graphs have one floating-point type; float values widen to it, and arguments are narrowed at the call (D-048).
                case SpecialType.System_Single: return TypeRef.Double;
            }

            if (type is ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Method } parameter)
            {
                return TypeRef.TypeParameter(parameter.Name);
            }

            if (type is not INamedTypeSymbol named)
            {
                return null;
            }

            // A type that converts implicitly to and from string, such as Godot's StringName and NodePath, is a string to graphs.
            if (ConvertsToAndFromString(named))
            {
                return TypeRef.String;
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
            context.Emit(new IrMemberAssignment(receiver, Method.Name, ExternalApi.Argument(Method, "value", context.Input("value"))));
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

/// <summary>
/// On: subscribes Handler to a .NET event, such as a Godot signal. Generates <c>instance.Event += (values) =&gt; { handler };</c>;
/// the values the event passes are outputs inside Handler only (D-051).
/// </summary>
public sealed class ExternalEventNode(ExternalMethod method) : StatementNodeDefinition(
    method.TypeId,
    method.DisplayName,
    ExternalApi.Category(method),
    ExternalApi.Description(method),
    [
        PinDefinition.ExecutionIn(),
        .. method.IsStatic ? [] : new[] { PinDefinition.DataIn("instance", method.DeclaringType.DisplayName, method.DeclaringType) },
        PinDefinition.Block("handler", "Handler"),
        .. method.Parameters.Select(parameter => PinDefinition.DataOut(parameter.Name, parameter.Name, parameter.Type)),
        PinDefinition.ExecutionOut(),
    ])
{
    public ExternalMethod Method { get; } = method;

    public override void Lower(StatementLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IrExpression receiver = Method.IsStatic ? new IrTypeReference(Method.DeclaringType) : context.Input("instance");
        IrLocal[] received = [.. Method.Parameters.Select(parameter => context.NewLocal(parameter.Name, parameter.Type))];
        IrBlock handler = context.LowerBlock("handler", NestedBlock.Handler, [.. Method.Parameters.Select((parameter, i) => (parameter.Name, received[i]))]);
        context.Emit(new IrEventSubscription(receiver, Method.Name, new IrLambda(received, handler, TypedParameters: false)));
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
