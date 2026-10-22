using ScriptArt.Compiler.Nodes;
using ScriptArt.GraphModel;

namespace ScriptArt.NodeEditor;

/// <summary>
/// What Go to Definition opens for a node: the graph that defines what the node uses, and within it the variable or property, if
/// the node uses one.
/// </summary>
public sealed record NodeNavigationTarget(string GraphId, string? VariableId = null, string? PropertyId = null);

public sealed partial class GraphEditor
{
    /// <summary>
    /// Where the node <paramref name="nodeId"/> is defined: a Call node's function or method, a New node's constructor (or its type
    /// when it runs none), a property node's class or struct, Self's type, and a variable node's variable in this graph. Null for
    /// nodes defined by the IDE or by .NET, which have no graph, and when the graph they name no longer exists.
    /// </summary>
    public NodeNavigationTarget? NavigationTargetOf(string nodeId)
    {
        if (Graph.FindNode(nodeId) is not { } node)
        {
            return null;
        }

        string? Property(string name) => node.Properties.GetValueOrDefault(name);
        NodeNavigationTarget? target = node.Type switch
        {
            FunctionNodes.CallTypeId when Property(FunctionNodes.FunctionProperty) is { } function => new(function),
            TypeNodes.CreateTypeId when Property(TypeNodes.ConstructorProperty) is { } constructor => new(constructor),
            TypeNodes.CreateTypeId when Property(TypeNodes.TypeProperty) is { } type => new(type),
            TypeNodes.GetTypeId or TypeNodes.SetTypeId or TypeNodes.WithTypeId when Property(TypeNodes.TypeProperty) is { } type =>
                new(type, PropertyId: Property(TypeNodes.PropertyProperty)),
            TypeNodes.SelfTypeId when Graph.Owner is { } owner => new(owner),
            VariableNodes.GetTypeId or VariableNodes.SetTypeId when Property(VariableNodes.VariableProperty) is { } variable
                && Graph.FindVariable(variable) is not null => new(Graph.Id, VariableId: variable),
            _ => null,
        };
        return target is not null && (target.GraphId == Graph.Id || ProjectGraphs.Any(graph => graph.Id == target.GraphId)) ? target : null;
    }
}
