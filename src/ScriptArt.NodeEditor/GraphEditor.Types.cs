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

    /// <summary>
    /// Sets the .NET class a class graph derives from, such as <c>Godot.CharacterBody2D</c>, or none for null or blank text. A
    /// class that is not offered or cannot be derived from is stored and reported by the compiler.
    /// </summary>
    public void SetBaseType(string? baseType)
    {
        string? old = Graph.BaseType;
        string? updated = string.IsNullOrWhiteSpace(baseType) ? null : baseType.Trim();
        if (old != updated)
        {
            Execute(new Edit("Change base class", () => Graph.BaseType = updated, () => Graph.BaseType = old));
        }
    }

    /// <summary>Declares a Godot signal with a unique name such as <c>Signal1</c> and no values (D-051).</summary>
    public GraphSignal AddSignal()
    {
        string name = "Signal1";
        for (int suffix = 2; Graph.Signals.Exists(signal => signal.Name == name); suffix++)
        {
            name = "Signal" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var added = new GraphSignal { Id = GraphDocument.NewId(), Name = name };
        Execute(new Edit("Add signal", () => Graph.Signals.Add(added), () => Graph.Signals.Remove(added)));
        return added;
    }

    /// <summary>Renames a signal; its Emit and On nodes refer to it by id and stay attached. Invalid names are reported by the compiler.</summary>
    public void RenameSignal(string signalId, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        GraphSignal signal = FindSignal(signalId);
        string old = signal.Name;
        if (old != name)
        {
            Execute(new Edit("Rename signal", () => signal.Name = name, () => signal.Name = old));
        }
    }

    /// <summary>
    /// Replaces the values a signal sends, written as in C#: <c>int damage, string source</c>. A value keeps its id, and so the
    /// wires of its pins, while its name stays. False, changing nothing, when the text is not such a list.
    /// </summary>
    public bool SetSignalParameters(string signalId, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        GraphSignal signal = FindSignal(signalId);
        if (ParseParameters(text) is not { } parsed)
        {
            return false;
        }

        GraphParameter[] old = [.. signal.Parameters];
        GraphParameter[] updated = [.. parsed.Select(parameter => old.FirstOrDefault(existing => existing.Name == parameter.Name) is { } kept
            ? new GraphParameter { Id = kept.Id, Name = parameter.Name, Type = parameter.Type }
            : new GraphParameter { Id = GraphDocument.NewId(), Name = parameter.Name, Type = parameter.Type })];
        if (!FormatParameters(old).Equals(FormatParameters(updated), StringComparison.Ordinal))
        {
            Execute(new Edit("Change signal values", () => Replace(updated), () => Replace(old)));
        }

        return true;

        void Replace(GraphParameter[] parameters)
        {
            signal.Parameters.Clear();
            signal.Parameters.AddRange(parameters);
        }
    }

    public void DeleteSignal(string signalId)
    {
        GraphSignal signal = FindSignal(signalId);
        int index = Graph.Signals.IndexOf(signal);
        Execute(new Edit("Delete signal", () => Graph.Signals.Remove(signal), () => Graph.Signals.Insert(index, signal)));
    }

    /// <summary>A parameter list as C# writes it, such as <c>int damage, string source</c>; empty for none.</summary>
    public static string FormatParameters(IEnumerable<GraphParameter> parameters) =>
        string.Join(", ", parameters.Select(parameter => $"{parameter.Type} {parameter.Name}"));

    /// <summary>
    /// Reads <c>int damage, string source</c> as (type, name) pairs; blank text is none. Null when an item is not a type followed by a
    /// name. Names and types are checked by the compiler; generic types, whose commas would split them, cannot be sent by signals.
    /// </summary>
    public static IReadOnlyList<(string Type, string Name)>? ParseParameters(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        List<(string, string)> parameters = [];
        foreach (string item in text.Split(','))
        {
            string[] words = item.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length != 2)
            {
                return null;
            }

            parameters.Add((words[0], words[1]));
        }

        return parameters;
    }

    private GraphSignal FindSignal(string signalId) =>
        Graph.Signals.Find(signal => signal.Id == signalId) ?? throw new ArgumentException($"Unknown signal '{signalId}'.", nameof(signalId));

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

    /// <summary>Exports a property to the Godot editor, or stops exporting it.</summary>
    public void SetPropertyExport(string propertyId, bool export)
    {
        GraphProperty property = FindProperty(propertyId);
        bool old = property.Export;
        if (old != export)
        {
            Execute(new Edit(export ? "Export property" : "Stop exporting property", () => property.Export = export, () => property.Export = old));
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
