using ScriptArt.GraphModel;

namespace ScriptArt.Compiler.TypeSystem;

/// <summary>
/// A reference to a .NET type used by pins and the intermediate representation.
/// </summary>
/// <param name="Namespace">
/// Containing namespace, used to generate <c>using</c> directives. Empty for a class or struct of the project, which is generated in
/// the root namespace together with the code that uses it, and for a type parameter.
/// </param>
/// <param name="Name">Metadata name without generic arity, for example <c>Int32</c> or <c>List</c>.</param>
/// <param name="Keyword">C# keyword alias, for example <c>int</c>, when one exists.</param>
/// <param name="Arguments">
/// The type arguments of a generic type: the item type of <c>List&lt;T&gt;</c>, the key and value types of a dictionary, or the
/// arguments of a generic class or struct of the project.
/// </param>
public sealed record TypeRef(string Namespace, string Name, string? Keyword = null, IReadOnlyList<TypeRef>? Arguments = null)
{
    public static readonly TypeRef Void = new("System", "Void", "void");
    public static readonly TypeRef Boolean = new("System", "Boolean", "bool");
    public static readonly TypeRef Int32 = new("System", "Int32", "int");
    public static readonly TypeRef Double = new("System", "Double", "double");
    public static readonly TypeRef String = new("System", "String", "string");
    public static readonly TypeRef Console = new("System", "Console");
    public static readonly TypeRef Exception = new("System", "Exception");
    public static readonly TypeRef InvalidOperationException = new("System", "InvalidOperationException");
    public static readonly TypeRef Task = new("System.Threading.Tasks", "Task");

    /// <summary>
    /// Any class: the type of inputs that take an object of every class, such as Cast To's. Values of classes connect to it as they
    /// are. It has no keyword, so a value is never converted to <c>object</c> on the way in.
    /// </summary>
    public static readonly TypeRef Object = new("System", "Object");

    private const string CollectionsNamespace = "System.Collections.Generic";

    public IReadOnlyList<TypeRef> Arguments { get; init; } = Arguments ?? [];

    /// <summary>True for a type parameter of a generic class or struct of the project, such as <c>T</c> in <c>Box&lt;T&gt;</c>.</summary>
    public bool IsTypeParameter { get; init; }

    /// <summary>The simple types of variables, parameters, and return values. Collections can also hold the project's classes and structs, and other collections.</summary>
    public static IReadOnlyList<TypeRef> ElementTypes { get; } = [Boolean, Int32, Double, String];

    /// <summary>The types a dictionary can be keyed by.</summary>
    public static IReadOnlyList<TypeRef> KeyTypes { get; } = [Int32, String];

    /// <summary><c>List&lt;T&gt;</c> of <paramref name="element"/>.</summary>
    public static TypeRef ListOf(TypeRef element) => new(CollectionsNamespace, "List", Arguments: [element]);

    /// <summary><c>Dictionary&lt;TKey, TValue&gt;</c> from <paramref name="key"/> to <paramref name="value"/>.</summary>
    public static TypeRef DictionaryOf(TypeRef key, TypeRef value) => new(CollectionsNamespace, "Dictionary", Arguments: [key, value]);

    /// <summary>A class or struct of the project; a generic one with its <paramref name="arguments"/>.</summary>
    public static TypeRef ProjectType(string name, IReadOnlyList<TypeRef>? arguments = null) => new("", name, Arguments: arguments);

    /// <summary>A type parameter of a generic class or struct of the project.</summary>
    public static TypeRef TypeParameter(string name) => new("", name) { IsTypeParameter = true };

    /// <summary>A non-generic .NET type from a namespace, such as <c>System.Text.StringBuilder</c>.</summary>
    public static TypeRef External(string ns, string name) => new(ns, name);

    /// <summary>
    /// The type with this C# name, as <see cref="SourceName"/> writes it: a simple type such as <c>int</c>, a type parameter in scope
    /// in <paramref name="context"/>, the name of a class or struct among <paramref name="projectGraphs"/> (with as many type
    /// arguments as it has type parameters, as in <c>Box&lt;int&gt;</c>), a list of any type, a dictionary from an
    /// <see cref="KeyTypes">int or string</see> to any type, or the full name of a non-generic .NET type such as
    /// <c>System.Text.StringBuilder</c>. Null when the name is none of these. Whether a .NET type exists is checked by the compiler,
    /// which knows the project's references.
    /// </summary>
    /// <param name="context">The graph the name is used in. Inside a generic class or struct and its methods, its type parameters are types.</param>
    public static TypeRef? FromName(string? name, IEnumerable<GraphDocument>? projectGraphs = null, GraphDocument? context = null)
    {
        if (name is null || TypeName.Parse(name) is not { } parsed)
        {
            return null;
        }

        GraphDocument[] graphs = [.. projectGraphs ?? []];
        return Resolve(parsed, graphs, TypeParametersOf(context, graphs));
    }

    /// <summary>
    /// The comma-separated type arguments in <paramref name="text"/>, such as <c>int, List&lt;string&gt;</c>, each read like
    /// <see cref="FromName"/>. Null when one of them is not a type.
    /// </summary>
    public static IReadOnlyList<TypeRef>? ArgumentsFromNames(string? text, IEnumerable<GraphDocument>? projectGraphs = null, GraphDocument? context = null)
    {
        // Read as the arguments of a generic name, so that nested arguments keep their commas.
        if (string.IsNullOrWhiteSpace(text) || TypeName.Parse($"_<{text}>") is not { } parsed)
        {
            return null;
        }

        GraphDocument[] graphs = [.. projectGraphs ?? []];
        IReadOnlyList<string> typeParameters = TypeParametersOf(context, graphs);
        TypeRef?[] arguments = [.. parsed.Arguments.Select(argument => Resolve(argument, graphs, typeParameters))];
        return arguments.Contains(null) ? null : [.. arguments.OfType<TypeRef>()];
    }

    /// <summary>Type arguments as <see cref="ArgumentsFromNames"/> reads them: <c>int, string</c>.</summary>
    public static string ArgumentNames(IEnumerable<TypeRef> arguments) => string.Join(", ", arguments.Select(argument => argument.SourceName));

    /// <summary>
    /// The type parameters that are types inside <paramref name="context"/>: those of a generic class or struct graph, or of the type
    /// that owns a method or constructor graph. Empty elsewhere.
    /// </summary>
    public static IReadOnlyList<string> TypeParametersOf(GraphDocument? context, IEnumerable<GraphDocument> projectGraphs)
    {
        ArgumentNullException.ThrowIfNull(projectGraphs);
        if (context is null)
        {
            return [];
        }

        if (IsTypeGraph(context))
        {
            return context.TypeParameters;
        }

        return context.Owner is { } owner && projectGraphs.FirstOrDefault(graph => graph.Id == owner && IsTypeGraph(graph)) is { } type
            ? type.TypeParameters
            : [];
    }

    private static TypeRef? Resolve(TypeName name, GraphDocument[] graphs, IReadOnlyList<string> typeParameters)
    {
        List<TypeRef> arguments = [];
        foreach (TypeName argument in name.Arguments)
        {
            if (Resolve(argument, graphs, typeParameters) is not { } resolved)
            {
                return null;
            }

            arguments.Add(resolved);
        }

        if (arguments.Count == 0)
        {
            if (ElementTypes.FirstOrDefault(type => type.Keyword == name.Name) is { } simple)
            {
                return simple;
            }

            if (typeParameters.Contains(name.Name, StringComparer.Ordinal))
            {
                return TypeParameter(name.Name);
            }
        }

        switch (name.Name, arguments.Count)
        {
            case ("List", 1):
                return ListOf(arguments[0]);
            case ("Dictionary", 2):
                return KeyTypes.Contains(arguments[0]) ? DictionaryOf(arguments[0], arguments[1]) : null;
        }

        if (graphs.FirstOrDefault(graph => IsTypeGraph(graph) && graph.Name == name.Name) is { } type)
        {
            return type.TypeParameters.Count == arguments.Count ? ProjectType(type.Name, arguments) : null;
        }

        // A dotted name is a .NET type. Generic .NET types other than List and Dictionary are not supported.
        int dot = name.Name.LastIndexOf('.');
        return dot > 0 && arguments.Count == 0 ? External(name.Name[..dot], name.Name[(dot + 1)..]) : null;
    }

    /// <summary>
    /// Every type a variable, parameter, return value, or property can have, in the order the editor offers them: the simple types,
    /// the type parameters in scope in <paramref name="context"/>, the project's classes and structs (a generic one with each simple
    /// type when it has one type parameter), the <paramref name="externalTypes"/>, then lists of all of them, then dictionaries of
    /// them. Collections of collections and generic types with more type parameters are written by name.
    /// </summary>
    public static IReadOnlyList<TypeRef> TypesOf(IEnumerable<GraphDocument> projectGraphs, GraphDocument? context = null, IEnumerable<TypeRef>? externalTypes = null)
    {
        GraphDocument[] graphs = [.. projectGraphs];
        IEnumerable<TypeRef> projectTypes = graphs.Where(IsTypeGraph).DistinctBy(graph => graph.Name).OrderBy(graph => graph.Name, StringComparer.Ordinal)
            .SelectMany(graph => graph.TypeParameters.Count switch
            {
                0 => [ProjectType(graph.Name)],
                1 => ElementTypes.Select(argument => ProjectType(graph.Name, [argument])),
                _ => [],
            });
        TypeRef[] values = [.. ElementTypes, .. TypeParametersOf(context, graphs).Select(TypeParameter), .. projectTypes, .. externalTypes ?? []];
        return [.. values, .. values.Select(ListOf), .. KeyTypes.SelectMany(key => values.Select(value => DictionaryOf(key, value)))];
    }

    /// <summary>True for a graph that defines a class or struct.</summary>
    public static bool IsTypeGraph(GraphDocument graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return graph.Kind is GraphKind.Class or GraphKind.Struct;
    }

    public bool IsList => Namespace == CollectionsNamespace && Name == "List" && Arguments.Count == 1;

    public bool IsDictionary => Namespace == CollectionsNamespace && Name == "Dictionary" && Arguments.Count == 2;

    /// <summary>The item type of a list, or the value type of a dictionary.</summary>
    public TypeRef? ElementType => IsList ? Arguments[0] : IsDictionary ? Arguments[1] : null;

    /// <summary>The key type of a dictionary, such as <c>string</c> in <c>Dictionary&lt;string, int&gt;</c>.</summary>
    public TypeRef? KeyType => IsDictionary ? Arguments[0] : null;

    /// <summary>True for a class or struct of the project.</summary>
    public bool IsProjectType => Namespace.Length == 0 && !IsTypeParameter;

    /// <summary>True for a .NET type that is neither a C# keyword type nor a list or dictionary, such as <c>System.Text.StringBuilder</c>.</summary>
    public bool IsExternal => Namespace.Length > 0 && Keyword is null && !IsList && !IsDictionary;

    /// <summary>The name shown in the editor and used in generated code.</summary>
    public string DisplayName => Keyword ?? (Arguments.Count == 0 ? Name : $"{Name}<{string.Join(", ", Arguments.Select(argument => argument.DisplayName))}>");

    /// <summary>
    /// The name graph files store and <see cref="FromName"/> reads: <see cref="DisplayName"/>, except that .NET types other than
    /// lists and dictionaries are written with their namespace, so that two types of the same name stay apart.
    /// </summary>
    public string SourceName => Keyword
        ?? (IsExternal ? $"{Namespace}.{Name}" : Arguments.Count == 0 ? Name : $"{Name}<{string.Join(", ", Arguments.Select(argument => argument.SourceName))}>");

    /// <summary>This type and every type argument in it, outermost first.</summary>
    public IEnumerable<TypeRef> SelfAndArguments() => Arguments.SelectMany(argument => argument.SelfAndArguments()).Prepend(this);

    /// <summary>This type with each type parameter in <paramref name="map"/> replaced, as in <c>Box&lt;T&gt;</c> with T = int.</summary>
    public TypeRef Substitute(IReadOnlyDictionary<string, TypeRef> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (IsTypeParameter)
        {
            return map.GetValueOrDefault(Name) ?? this;
        }

        return Arguments.Count == 0 ? this : this with { Arguments = [.. Arguments.Select(argument => argument.Substitute(map))] };
    }

    public bool Equals(TypeRef? other) =>
        other is not null && Namespace == other.Namespace && Name == other.Name && Keyword == other.Keyword
        && IsTypeParameter == other.IsTypeParameter && Arguments.SequenceEqual(other.Arguments);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Namespace);
        hash.Add(Name);
        hash.Add(IsTypeParameter);
        foreach (TypeRef argument in Arguments)
        {
            hash.Add(argument);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => DisplayName;

    /// <summary>A C# type name split into its name and type arguments: <c>Dictionary&lt;string, List&lt;int&gt;&gt;</c>.</summary>
    private sealed record TypeName(string Name, IReadOnlyList<TypeName> Arguments)
    {
        /// <summary>Null when <paramref name="text"/> is not a dotted name with optional, possibly nested, type arguments.</summary>
        public static TypeName? Parse(string text)
        {
            int position = 0;
            TypeName? name = ParseAt(text, ref position);
            return name is not null && position == text.Length ? name : null;
        }

        private static TypeName? ParseAt(string text, ref int position)
        {
            int start = position;
            while (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] is '_' or '.'))
            {
                position++;
            }

            string identifier = text[start..position];
            if (identifier.Length == 0 || identifier.Split('.').Any(part => part.Length == 0 || char.IsDigit(part[0])))
            {
                return null;
            }

            List<TypeName> arguments = [];
            if (position < text.Length && text[position] == '<')
            {
                do
                {
                    position++;
                    while (position < text.Length && text[position] == ' ')
                    {
                        position++;
                    }

                    if (ParseAt(text, ref position) is not { } argument)
                    {
                        return null;
                    }

                    arguments.Add(argument);
                }
                while (position < text.Length && text[position] == ',');

                if (position >= text.Length || text[position] != '>')
                {
                    return null;
                }

                position++;
            }

            return new TypeName(identifier, arguments);
        }
    }
}

/// <summary>
/// Conversion rules between pin types. Only conversions that C# performs implicitly and losslessly are allowed;
/// everything else requires an explicit conversion node.
/// </summary>
public static class TypeConversions
{
    public static bool IsImplicitlyConvertible(TypeRef source, TypeRef target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        return source == target || (source == TypeRef.Int32 && target == TypeRef.Double);
    }
}
