using VisualStudioLikeIDE.Compiler.Nodes;
using VisualStudioLikeIDE.GraphModel;
using VisualStudioLikeIDE.NodeEditor;

namespace VisualStudioLikeIDE.Tests;

public sealed class ActionMenuTests
{
    private static readonly NodeLibrary Library = NodeLibrary.CreateDefault();

    [Fact]
    public void ContextSensitiveActionsLeaveOutASecondEntryNode()
    {
        var program = new GraphBuilder();
        program.Node(BuiltInNodes.EntryTypeId);
        var editor = new GraphEditor(program.Graph, Library);

        Assert.DoesNotContain(editor.Actions([], contextSensitive: true), definition => definition.TypeId == BuiltInNodes.EntryTypeId);
        Assert.Contains(editor.Actions([], contextSensitive: false), definition => definition.TypeId == BuiltInNodes.EntryTypeId);
    }

    [Fact]
    public void ContextSensitiveActionsFromAPinOfferOnlyNodesThatConnectToIt()
    {
        var program = new GraphBuilder();
        string text = program.Node("literal.string", "Hi");
        var editor = new GraphEditor(program.Graph, Library);
        PinRef[] anchors = [new(text, "value")];

        NodeDefinition[] sensitive = [.. editor.Actions(anchors, contextSensitive: true)];

        Assert.Contains(sensitive, definition => definition.TypeId == "console.write-line");
        Assert.DoesNotContain(sensitive, definition => definition.TypeId == "math.int.add");
        Assert.Contains(editor.Actions(anchors, contextSensitive: false), definition => definition.TypeId == "math.int.add");
    }

    [Fact]
    public void FreePinsAreUnconnectedInputsAndExecutionOutputsAndEveryDataOutput()
    {
        var program = new GraphBuilder();
        string entry = program.Node(BuiltInNodes.EntryTypeId);
        string print = program.PrintLiteral("Hi");
        string text = program.Node("literal.string", "Hi");
        program.Then(entry, print);
        var editor = new GraphEditor(program.Graph, Library);

        Assert.Empty(editor.FreePins(entry));
        Assert.Equal([new PinRef(print, "then")], editor.FreePins(print));
        Assert.Equal([new PinRef(text, "value")], editor.FreePins(text));
    }

    [Fact]
    public void ActionNodeConnectsToTheFirstAnchorItFitsAsOneUndoStep()
    {
        var program = new GraphBuilder();
        string text = program.Node("literal.string", "Hi");
        string number = program.Node("literal.int", "1");
        var editor = new GraphEditor(program.Graph, Library);

        GraphNode print = editor.AddActionNode(Library.Find("console.write-line")!, 200, 0, [new(number, "value"), new(text, "value")]);

        Assert.Equal(new GraphConnection(new PinRef(text, "value"), new PinRef(print.Id, "text")), Assert.Single(program.Graph.Connections));
        editor.Undo();
        Assert.Equal(2, program.Graph.Nodes.Count);
        Assert.Empty(program.Graph.Connections);

        editor.AddActionNode(Library.Find("math.int.add")!, 200, 0, [new(text, "value")]);
        Assert.Equal(3, program.Graph.Nodes.Count);
        Assert.Empty(program.Graph.Connections);
    }
}
