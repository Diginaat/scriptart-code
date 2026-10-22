using VisualStudioLikeIDE.Compiler.Nodes;
using VisualStudioLikeIDE.Compiler.TypeSystem;
using VisualStudioLikeIDE.GraphModel;

namespace VisualStudioLikeIDE.NodeEditor;

/// <summary>Graph variables: adding, renaming, retyping, and removing them, each as one undo step.</summary>
public sealed partial class GraphEditor
{
    /// <summary>Adds a variable of the given type (a C# keyword such as <c>int</c>) with a unique name and the type's default value.</summary>
    public GraphVariable AddVariable(string type)
    {
        TypeRef typeRef = TypeRef.FromName(type) ?? throw new ArgumentException($"Unsupported variable type '{type}'.", nameof(type));
        string name = "variable";
        for (int suffix = 2; Graph.Variables.Exists(variable => variable.Name == name); suffix++)
        {
            name = "variable" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var added = new GraphVariable { Id = GraphDocument.NewId(), Name = name, Type = type, Value = DefaultValue(typeRef) };
        Execute(new Edit("Add variable", () => Graph.Variables.Add(added), () => Graph.Variables.Remove(added)));
        return added;
    }

    /// <summary>Renames a variable. Its nodes keep referring to it by id. Invalid names are stored and reported by the compiler.</summary>
    public void RenameVariable(string variableId, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        GraphVariable variable = FindVariable(variableId);
        string old = variable.Name;
        if (old != name)
        {
            Execute(new Edit("Rename variable", () => variable.Name = name, () => variable.Name = old));
        }
    }

    public void SetVariableValue(string variableId, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        GraphVariable variable = FindVariable(variableId);
        string old = variable.Value;
        if (old != value)
        {
            Execute(new Edit("Change initial value", () => variable.Value = value, () => variable.Value = old));
        }
    }

    /// <summary>
    /// Changes a variable's type. Wires to its Get and Set nodes that the new type no longer allows are removed, and an initial
    /// value that does not fit the new type is reset to the type's default, all in the same undo step.
    /// </summary>
    public void SetVariableType(string variableId, string type)
    {
        GraphVariable variable = FindVariable(variableId);
        TypeRef typeRef = TypeRef.FromName(type) ?? throw new ArgumentException($"Unsupported variable type '{type}'.", nameof(type));
        (string oldType, string oldValue) = (variable.Type, variable.Value);
        if (oldType == type)
        {
            return;
        }

        string newValue = LiteralValues.TryInitialValue(typeRef, variable.Value, out _) is not null ? variable.Value : DefaultValue(typeRef);
        ExecuteDroppingInvalidWires(
            "Change variable type",
            () => (variable.Type, variable.Value) = (type, newValue),
            () => (variable.Type, variable.Value) = (oldType, oldValue),
            NodesUsing(variableId));
    }

    /// <summary>
    /// Runs a change that alters the pins of <paramref name="affectedNodes"/> as one undo step, removing the wires on those nodes
    /// that the changed pins no longer allow. Undo restores the wires in their original order.
    /// </summary>
    private void ExecuteDroppingInvalidWires(string description, Action apply, Action revert, HashSet<string> affectedNodes)
    {
        // Check the wires against the changed pins, then undo the change until the edit runs.
        apply();
        List<(int Index, GraphConnection Connection)> invalid = [.. Graph.Connections
            .Select((connection, index) => (index, connection))
            .Where(entry => (affectedNodes.Contains(entry.connection.From.Node) || affectedNodes.Contains(entry.connection.To.Node))
                && !CanConnect(entry.connection.From, entry.connection.To, out _).IsAllowed)];
        revert();

        Execute(new Edit(
            description,
            () =>
            {
                apply();
                foreach ((_, GraphConnection connection) in invalid)
                {
                    Graph.Connections.Remove(connection);
                }
            },
            () =>
            {
                revert();
                foreach ((int index, GraphConnection connection) in invalid)
                {
                    Graph.Connections.Insert(index, connection);
                }
            }));
    }

    /// <summary>Removes a variable together with its Get and Set nodes and their wires.</summary>
    public void DeleteVariable(string variableId)
    {
        GraphVariable variable = FindVariable(variableId);
        int index = Graph.Variables.IndexOf(variable);
        Edit? nodes = CreateDeleteEdit(NodesUsing(variableId));
        Execute(new Edit(
            "Delete variable",
            () =>
            {
                nodes?.Apply();
                Graph.Variables.RemoveAt(index);
            },
            () =>
            {
                Graph.Variables.Insert(index, variable);
                nodes?.Revert();
            }));
    }

    /// <summary>The ids of the Get and Set nodes that refer to a variable.</summary>
    public HashSet<string> NodesUsing(string variableId) =>
        [.. Graph.Nodes
            .Where(node => node.Type is VariableNodes.GetTypeId or VariableNodes.SetTypeId
                && node.Properties.GetValueOrDefault(VariableNodes.VariableProperty) == variableId)
            .Select(node => node.Id)];

    private GraphVariable FindVariable(string variableId) =>
        Graph.FindVariable(variableId) ?? throw new ArgumentException($"Unknown variable '{variableId}'.", nameof(variableId));

    private static string DefaultValue(TypeRef type) =>
        type == TypeRef.Boolean ? "false" : type == TypeRef.String || type.IsList ? "" : "0";
}
