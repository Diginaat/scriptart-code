using ScriptArt.Compiler.Nodes;
using ScriptArt.GraphModel;

namespace ScriptArt.NodeEditor;

/// <summary>
/// The action menu (right-click on the canvas, or a wire released on empty space): which nodes it offers, and adding the chosen
/// one connected to the pin it was opened for.
/// </summary>
public sealed partial class GraphEditor
{
    /// <summary>
    /// The nodes the action menu offers. Without context sensitivity: every node that can be added to the graph. With it: only nodes
    /// that fit, which leaves out a second Entry or Function Entry node and, when <paramref name="anchors"/> is not empty, every node
    /// without a pin that can connect to one of the anchors.
    /// </summary>
    /// <param name="anchors">The pins a new node would connect to: the pin a wire was dragged from, or the free pins of the selected node.</param>
    public IEnumerable<NodeDefinition> Actions(IReadOnlyList<PinRef> anchors, bool contextSensitive)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        if (!contextSensitive)
        {
            return AvailableDefinitions;
        }

        HashSet<string> present = [.. Graph.Nodes.Select(node => node.Type)];
        return AvailableDefinitions.Where(definition =>
            !(definition is EntryNode or FunctionEntryNode && present.Contains(definition.TypeId))
            && (anchors.Count == 0 || anchors.Any(anchor => FindCompatiblePin(definition, anchor) is not null)));
    }

    /// <summary>
    /// The pins of a node that a new node could still be wired to: unconnected inputs and execution outputs, and every data output,
    /// since a value can feed any number of inputs. Outputs come first, because a new node usually continues from the selected one.
    /// </summary>
    public IReadOnlyList<PinRef> FreePins(string nodeId)
    {
        if (Graph.FindNode(nodeId) is not { } node || DefinitionOf(node) is not { } definition)
        {
            return [];
        }

        bool Connected(PinDefinition pin) => Graph.Connections.Exists(connection =>
            pin.Direction == PinDirection.Input
                ? connection.To == new PinRef(nodeId, pin.Id)
                : connection.From == new PinRef(nodeId, pin.Id));
        return [.. definition.Pins
            .Where(pin => pin.Direction == PinDirection.Output ? !pin.IsExecution || !Connected(pin) : !Connected(pin))
            .OrderBy(pin => pin.Direction == PinDirection.Output ? 0 : 1)
            .Select(pin => new PinRef(nodeId, pin.Id))];
    }

    /// <summary>
    /// Adds a node chosen in the action menu, connected to the first of <paramref name="anchors"/> it fits, as one undo step; a node
    /// that fits none (context sensitivity turned off) is added unconnected.
    /// </summary>
    public GraphNode AddActionNode(NodeDefinition definition, double x, double y, IReadOnlyList<PinRef> anchors)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(anchors);
        return anchors.FirstOrDefault(anchor => FindCompatiblePin(definition, anchor) is not null) is { } source
            ? AddConnectedNode(definition, x, y, source)
            : AddNode(definition, x, y);
    }
}
