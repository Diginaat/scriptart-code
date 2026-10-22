using VisualStudioLikeIDE.GraphModel;

namespace VisualStudioLikeIDE.Compiler.TypeSystem;

/// <summary>
/// A reference to a .NET type used by pins and the intermediate representation.
/// </summary>
/// <param name="Namespace">
/// Containing namespace, used to generate <c>using</c> directives. Empty for a class or struct of the project, which is generated in
/// the root namespace together with the code that uses it.
/// </param>
/// <param name="Name">Metadata name without generic arity, for example <c>Int32</c> or <c>List</c>.</param>
/// <param name="Keyword">C# keyword alias, for example <c>int</c>, when one exists.</param>
/// <param name="ElementType">The type argument of a generic collection such as <c>List&lt;int&gt;</c>.</param>
public sealed record TypeRef(string Namespace, string Name, string? Keyword = null, TypeRef? ElementType = null)
{
    public static readonly TypeRef Void = new("System", "Void", "void");
    public static readonly TypeRef Boolean = new("System", "Boolean", "bool");
    public static readonly TypeRef Int32 = new("System", "Int32", "int");
    public static readonly TypeRef Double = new("System", "Double", "double");
    public static readonly TypeRef String = new("System", "String", "string");
    public static readonly TypeRef Console = new("System", "Console");
    public static readonly TypeRef Exception = new("System", "Exception");
    public static readonly TypeRef InvalidOperationException = new("System", "InvalidOperationException");

    /// <summary>The types a list can hold, which are also the simple types of variables, parameters, and return values.</summary>
    public static IReadOnlyList<TypeRef> ElementTypes { get; } = [Boolean, Int32, Double, String];

    /// <summary>The types a graph variable, parameter, or return value can have, in the order the editor offers them.</summary>
    public static IReadOnlyList<TypeRef> VariableTypes { get; } = [.. ElementTypes, .. ElementTypes.Select(ListOf)];

    /// <summary><c>List&lt;T&gt;</c> of <paramref name="element"/>.</summary>
    public static TypeRef ListOf(TypeRef element) => new("System.Collections.Generic", "List", ElementType: element);

    /// <summary>A class or struct of the project.</summary>
    public static TypeRef ProjectType(string name) => new("", name);

    /// <summary>
    /// The variable type with this C# name, such as <c>int</c>, <c>List&lt;int&gt;</c>, or the name of a class or struct among
    /// <paramref name="projectGraphs"/>; null when there is none.
    /// </summary>
    public static TypeRef? FromName(string? name, IEnumerable<GraphDocument>? projectGraphs = null) =>
        VariableTypes.FirstOrDefault(type => type.DisplayName == name)
        ?? (projectGraphs ?? []).Where(IsTypeGraph).Where(graph => graph.Name == name).Select(graph => ProjectType(graph.Name)).FirstOrDefault();

    /// <summary>Every type a variable, parameter, return value, or property can have: <see cref="VariableTypes"/>, then the project's classes and structs.</summary>
    public static IReadOnlyList<TypeRef> TypesOf(IEnumerable<GraphDocument> projectGraphs) =>
        [.. VariableTypes, .. projectGraphs.Where(IsTypeGraph).Select(graph => graph.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(ProjectType)];

    /// <summary>True for a graph that defines a class or struct.</summary>
    public static bool IsTypeGraph(GraphDocument graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return graph.Kind is GraphKind.Class or GraphKind.Struct;
    }

    public bool IsList => Namespace == "System.Collections.Generic" && Name == "List" && ElementType is not null;

    /// <summary>True for a class or struct of the project.</summary>
    public bool IsProjectType => Namespace.Length == 0;

    /// <summary>The name shown in the editor and used in generated code.</summary>
    public string DisplayName => Keyword ?? (ElementType is null ? Name : $"{Name}<{ElementType.DisplayName}>");

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
