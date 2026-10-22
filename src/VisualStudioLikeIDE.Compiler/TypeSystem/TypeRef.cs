namespace VisualStudioLikeIDE.Compiler.TypeSystem;

/// <summary>
/// A reference to a .NET type used by pins and the intermediate representation.
/// </summary>
/// <param name="Namespace">Containing namespace, used to generate <c>using</c> directives.</param>
/// <param name="Name">Metadata name, for example <c>Int32</c>.</param>
/// <param name="Keyword">C# keyword alias, for example <c>int</c>, when one exists.</param>
public sealed record TypeRef(string Namespace, string Name, string? Keyword = null)
{
    public static readonly TypeRef Void = new("System", "Void", "void");
    public static readonly TypeRef Boolean = new("System", "Boolean", "bool");
    public static readonly TypeRef Int32 = new("System", "Int32", "int");
    public static readonly TypeRef Double = new("System", "Double", "double");
    public static readonly TypeRef String = new("System", "String", "string");
    public static readonly TypeRef Console = new("System", "Console");

    /// <summary>The types a graph variable can have, in the order the editor offers them.</summary>
    public static IReadOnlyList<TypeRef> VariableTypes { get; } = [Boolean, Int32, Double, String];

    /// <summary>The variable type with this C# keyword, or null.</summary>
    public static TypeRef? FromKeyword(string? keyword) => VariableTypes.FirstOrDefault(type => type.Keyword == keyword);

    /// <summary>The name shown in the editor and used in generated code.</summary>
    public string DisplayName => Keyword ?? Name;

    public override string ToString() => DisplayName;
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
