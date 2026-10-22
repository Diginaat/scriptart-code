using ScriptArt.Compiler.Nodes;
using ScriptArt.Compiler.TypeSystem;
using ScriptArt.GraphModel;

namespace ScriptArt.NodeEditor;

/// <summary>
/// The signature of a function graph: its name, parameters, and return type, each change as one undo step. Wires in this graph
/// that a change makes invalid are removed with it; call nodes in other graphs are checked by the compiler.
/// </summary>
public sealed partial class GraphEditor
{
    /// <summary>Renames the graph: a function, class, or struct. Nodes that refer to it keep working because they store its id.</summary>
    public void RenameGraph(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string old = Graph.Name;
        if (old != name)
        {
            Execute(new Edit($"Rename {Graph.Kind.ToString().ToLowerInvariant()}", () => Graph.Name = name, () => Graph.Name = old));
        }
    }

    /// <summary>Adds a parameter of the given type (a C# keyword such as <c>int</c>) with a unique name.</summary>
    public GraphParameter AddParameter(string type)
    {
        _ = ResolveType(type, "parameter");
        string name = "value";
        for (int suffix = 2; Graph.Parameters.Exists(parameter => parameter.Name == name) || Graph.Variables.Exists(variable => variable.Name == name); suffix++)
        {
            name = "value" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var added = new GraphParameter { Id = GraphDocument.NewId(), Name = name, Type = type };
        Execute(new Edit("Add parameter", () => Graph.Parameters.Add(added), () => Graph.Parameters.Remove(added)));
        return added;
    }

    /// <summary>Renames a parameter. Wires stay attached because pins are identified by the parameter's id.</summary>
    public void RenameParameter(string parameterId, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        GraphParameter parameter = FindParameter(parameterId);
        string old = parameter.Name;
        if (old != name)
        {
            Execute(new Edit("Rename parameter", () => parameter.Name = name, () => parameter.Name = old));
        }
    }

    public void SetParameterType(string parameterId, string type)
    {
        _ = ResolveType(type, "parameter");
        GraphParameter parameter = FindParameter(parameterId);
        string old = parameter.Type;
        if (old != type)
        {
            ExecuteDroppingInvalidWires("Change parameter type", () => parameter.Type = type, () => parameter.Type = old, FunctionNodesInGraph());
        }
    }

    /// <summary>Removes a parameter and the wires from its pin.</summary>
    public void DeleteParameter(string parameterId)
    {
        GraphParameter parameter = FindParameter(parameterId);
        int index = Graph.Parameters.IndexOf(parameter);
        ExecuteDroppingInvalidWires(
            "Delete parameter",
            () => Graph.Parameters.RemoveAt(index),
            () => Graph.Parameters.Insert(index, parameter),
            FunctionNodesInGraph());
    }

    /// <summary>Sets the return type: a C# keyword such as <c>int</c>, or null for a function that returns nothing.</summary>
    public void SetReturnType(string? type)
    {
        if (type is not null)
        {
            _ = ResolveType(type, "return");
        }

        string? old = Graph.ReturnType;
        if (old != type)
        {
            ExecuteDroppingInvalidWires("Change return type", () => Graph.ReturnType = type, () => Graph.ReturnType = old, FunctionNodesInGraph());
        }
    }

    // Entry, Return Value, and call nodes (a function can call itself) are the nodes whose pins come from a signature.
    private HashSet<string> FunctionNodesInGraph() =>
        [.. Graph.Nodes
            .Where(node => node.Type is FunctionNodes.EntryTypeId or FunctionNodes.ReturnTypeId or FunctionNodes.CallTypeId)
            .Select(node => node.Id)];

    private GraphParameter FindParameter(string parameterId) =>
        Graph.FindParameter(parameterId) ?? throw new ArgumentException($"Unknown parameter '{parameterId}'.", nameof(parameterId));
}
