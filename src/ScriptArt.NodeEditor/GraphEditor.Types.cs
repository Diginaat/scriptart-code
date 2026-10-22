using ScriptArt.Compiler.Nodes;
using ScriptArt.Compiler.TypeSystem;
using ScriptArt.GraphModel;

namespace ScriptArt.NodeEditor;

/// <summary>
/// The properties of a class or struct graph, each change as one undo step. Property nodes in other graphs refer to a property by
/// id, so a rename keeps them attached; wires that a type change makes invalid there are reported by the compiler.
/// </summary>
public sealed partial class GraphEditor
{
    /// <summary>Adds a property of the given type (a C# name such as <c>int</c>) with a unique name and the type's default value.</summary>
    public GraphProperty AddProperty(string type)
    {
        TypeRef typeRef = ResolveType(type, "property");
        string name = "Value";
        for (int suffix = 2; Graph.Properties.Exists(property => property.Name == name); suffix++)
        {
            name = "Value" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var added = new GraphProperty { Id = GraphDocument.NewId(), Name = name, Type = type, Value = DefaultValue(typeRef) };
        Execute(new Edit("Add property", () => Graph.Properties.Add(added), () => Graph.Properties.Remove(added)));
        return added;
    }

    /// <summary>
    /// Replaces the type parameters of a generic class or struct, such as <c>T</c>. Invalid or duplicate names are stored and
    /// reported by the compiler.
    /// </summary>
    public void SetTypeParameters(IReadOnlyList<string> typeParameters)
    {
        ArgumentNullException.ThrowIfNull(typeParameters);
        string[] old = [.. Graph.TypeParameters];
        string[] updated = [.. typeParameters];
        if (!old.SequenceEqual(updated, StringComparer.Ordinal))
        {
            Execute(new Edit("Change type parameters", () => Replace(updated), () => Replace(old)));
        }

        void Replace(string[] names)
        {
            Graph.TypeParameters.Clear();
            Graph.TypeParameters.AddRange(names);
        }
    }

    /// <summary>Renames a property. Invalid names are stored and reported by the compiler.</summary>
    public void RenameProperty(string propertyId, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        GraphProperty property = FindProperty(propertyId);
        string old = property.Name;
        if (old != name)
        {
            Execute(new Edit("Rename property", () => property.Name = name, () => property.Name = old));
        }
    }

    /// <summary>Changes a property's type. An initial value that does not fit the new type is reset to the type's default in the same undo step.</summary>
    public void SetPropertyType(string propertyId, string type)
    {
        GraphProperty property = FindProperty(propertyId);
        TypeRef typeRef = ResolveType(type, "property");
        (string oldType, string oldValue) = (property.Type, property.Value);
        if (oldType == type)
        {
            return;
        }

        string newValue = LiteralValues.TryInitialValue(typeRef, property.Value, out _) is not null ? property.Value : DefaultValue(typeRef);
        Execute(new Edit(
            "Change property type",
            () => (property.Type, property.Value) = (type, newValue),
            () => (property.Type, property.Value) = (oldType, oldValue)));
    }

    public void SetPropertyValue(string propertyId, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        GraphProperty property = FindProperty(propertyId);
        string old = property.Value;
        if (old != value)
        {
            Execute(new Edit("Change initial value", () => property.Value = value, () => property.Value = old));
        }
    }

    public void DeleteProperty(string propertyId)
    {
        GraphProperty property = FindProperty(propertyId);
        int index = Graph.Properties.IndexOf(property);
        Execute(new Edit("Delete property", () => Graph.Properties.RemoveAt(index), () => Graph.Properties.Insert(index, property)));
    }

    private GraphProperty FindProperty(string propertyId) =>
        Graph.FindProperty(propertyId) ?? throw new ArgumentException($"Unknown property '{propertyId}'.", nameof(propertyId));
}
