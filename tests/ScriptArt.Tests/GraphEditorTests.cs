using ScriptArt.Compiler.Diagnostics;
using ScriptArt.Compiler.Lowering;
using ScriptArt.Compiler.Nodes;
using ScriptArt.GraphModel;
using ScriptArt.NodeEditor;

namespace ScriptArt.Tests;

public sealed class GraphEditorTests
{
    private static GraphEditor NewEditor() =>
        new(GraphDocument.CreateEmpty("Program", GraphKind.EntryPoint), NodeLibrary.CreateDefault());

    [Fact]
    public void AddNodeUsesDefinitionDefaultsAndIsUndoable()
    {
        GraphEditor editor = NewEditor();
        int changes = 0;
        editor.Changed += (_, _) => changes++;

        GraphNode literal = editor.AddNode("literal.string", 10, 20);
        Assert.Equal("Hello, World!", literal.Properties["value"]);
        Assert.True(editor.IsDirty);

        editor.Undo();
        Assert.Empty(editor.Graph.Nodes);
        editor.Redo();
        Assert.Same(literal, Assert.Single(editor.Graph.Nodes));
        Assert.Equal(3, changes);
    }

    [Fact]
    public void ConnectAcceptsEitherDragDirection()
    {
        GraphEditor editor = NewEditor();
        GraphNode print = editor.AddNode("console.write-line", 0, 0);
        GraphNode text = editor.AddNode("literal.string", 0, 0);

        ConnectionCheck check = editor.Connect(new PinRef(print.Id, "text"), new PinRef(text.Id, "value"));

        Assert.True(check.IsAllowed);
        Assert.Equal(new GraphConnection(new PinRef(text.Id, "value"), new PinRef(print.Id, "text")), Assert.Single(editor.Graph.Connections));
    }

    [Fact]
    public void IncompatibleConnectionIsRejectedWithReason()
    {
        GraphEditor editor = NewEditor();
        GraphNode print = editor.AddNode("console.write-line", 0, 0);
        GraphNode number = editor.AddNode("literal.int", 0, 0);

        ConnectionCheck check = editor.Connect(new PinRef(number.Id, "value"), new PinRef(print.Id, "text"));

        Assert.False(check.IsAllowed);
        Assert.Equal(DiagnosticIds.TypeMismatch, check.ErrorId);
        Assert.Empty(editor.Graph.Connections);
    }

    [Fact]
    public void ConnectingDataInputReplacesPreviousSourceInOneUndoStep()
    {
        GraphEditor editor = NewEditor();
        GraphNode print = editor.AddNode("console.write-line", 0, 0);
        GraphNode first = editor.AddNode("literal.string", 0, 0);
        GraphNode second = editor.AddNode("literal.string", 0, 0);
        var input = new PinRef(print.Id, "text");
        editor.Connect(new PinRef(first.Id, "value"), input);
        editor.Connect(new PinRef(second.Id, "value"), input);

        Assert.Equal(second.Id, Assert.Single(editor.Graph.Connections).From.Node);
        editor.Undo();
        Assert.Equal(first.Id, Assert.Single(editor.Graph.Connections).From.Node);
    }

    [Fact]
    public void ExecutionOutputKeepsSingleTargetButInputAcceptsMany()
    {
        GraphEditor editor = NewEditor();
        GraphNode entry = editor.AddNode(BuiltInNodes.EntryTypeId, 0, 0);
        GraphNode a = editor.AddNode("console.write-line", 0, 0);
        GraphNode b = editor.AddNode("console.write-line", 0, 0);
        GraphNode c = editor.AddNode("console.write-line", 0, 0);
        editor.Connect(new PinRef(entry.Id, "then"), new PinRef(a.Id, "exec"));
        editor.Connect(new PinRef(entry.Id, "then"), new PinRef(b.Id, "exec"));
        editor.Connect(new PinRef(a.Id, "then"), new PinRef(c.Id, "exec"));
        editor.Connect(new PinRef(b.Id, "then"), new PinRef(c.Id, "exec"));

        Assert.DoesNotContain(editor.Graph.Connections, connection => connection.To.Node == a.Id);
        Assert.Equal(2, editor.Graph.Connections.Count(connection => connection.To.Node == c.Id));
    }

    [Fact]
    public void RemovingNodeRemovesConnectionsAndUndoRestoresOrder()
    {
        GraphEditor editor = NewEditor();
        GraphNode print = editor.AddNode("console.write-line", 0, 0);
        GraphNode text = editor.AddNode("literal.string", 0, 0);
        GraphNode other = editor.AddNode("literal.int", 0, 0);
        editor.Connect(new PinRef(text.Id, "value"), new PinRef(print.Id, "text"));
        string before = GraphSerializer.Serialize(editor.Graph);

        editor.Delete([text.Id]);
        Assert.Empty(editor.Graph.Connections);
        Assert.Equal([print, other], editor.Graph.Nodes);

        editor.Undo();
        Assert.Equal(before, GraphSerializer.Serialize(editor.Graph));
    }

    [Fact]
    public void MoveIsOneUndoStep()
    {
        GraphEditor editor = NewEditor();
        GraphNode node = editor.AddNode("literal.int", 0, 0);
        node.X = 100;
        node.Y = 50;
        editor.CommitMove([new ItemMove(node.Id, 0, 0, 100, 50)]);

        editor.Undo();
        Assert.Equal((0.0, 0.0), (node.X, node.Y));
        editor.Redo();
        Assert.Equal((100.0, 50.0), (node.X, node.Y));
    }

    [Fact]
    public void DirtyTracksSavedState()
    {
        GraphEditor editor = NewEditor();
        GraphNode node = editor.AddNode("literal.int", 0, 0);
        editor.MarkSaved();
        Assert.False(editor.IsDirty);

        editor.SetProperty(node.Id, "value", "5");
        Assert.True(editor.IsDirty);
        editor.Undo();
        Assert.False(editor.IsDirty);
        Assert.Equal("0", node.Properties["value"]);
    }

    [Fact]
    public void CompatiblePinFollowsConnectionRulesInBothDirections()
    {
        GraphEditor editor = NewEditor();
        GraphNode literal = editor.AddNode("literal.int", 0, 0);
        GraphNode print = editor.AddNode("console.write-line", 0, 0);
        NodeLibrary library = editor.Library;

        // From an int output: int and double inputs accept it, string inputs and execution pins do not.
        var intOutput = new PinRef(literal.Id, "value");
        Assert.Equal("a", editor.FindCompatiblePin(library.Find("math.double.add")!, intOutput)?.Id);
        Assert.Equal("value", editor.FindCompatiblePin(library.Find("convert.int-to-string")!, intOutput)?.Id);
        Assert.Null(editor.FindCompatiblePin(library.Find("console.write-line")!, intOutput));
        Assert.Null(editor.FindCompatiblePin(library.Find("literal.int")!, intOutput));

        // Into a string input: only nodes producing a string.
        var textInput = new PinRef(print.Id, "text");
        Assert.Equal("line", editor.FindCompatiblePin(library.Find("console.read-line")!, textInput)?.Id);
        Assert.Null(editor.FindCompatiblePin(library.Find("literal.int")!, textInput));

        // From an execution output: the execution input of statement nodes.
        var then = new PinRef(print.Id, "then");
        Assert.Equal("exec", editor.FindCompatiblePin(library.Find("core.branch")!, then)?.Id);
        Assert.Null(editor.FindCompatiblePin(library.Find("logic.not")!, then));
    }

    [Fact]
    public void AddConnectedNodeIsOneUndoStepAndReplacesSingleConnection()
    {
        GraphEditor editor = NewEditor();
        GraphNode print = editor.AddNode("console.write-line", 0, 0);
        GraphNode oldText = editor.AddNode("literal.string", 0, 0);
        editor.Connect(new PinRef(oldText.Id, "value"), new PinRef(print.Id, "text"));
        GraphConnection old = Assert.Single(editor.Graph.Connections);
        int changes = 0;
        editor.Changed += (_, _) => changes++;

        GraphNode read = editor.AddConnectedNode("console.read-line", 50, 60, new PinRef(print.Id, "text"));

        Assert.Equal(1, changes);
        Assert.Equal((50.0, 60.0), (read.X, read.Y));
        Assert.Equal(new GraphConnection(new PinRef(read.Id, "line"), new PinRef(print.Id, "text")), Assert.Single(editor.Graph.Connections));
        Assert.Equal("Add Read Line", editor.UndoDescription);

        editor.Undo();
        Assert.DoesNotContain(read, editor.Graph.Nodes);
        Assert.Equal(old, Assert.Single(editor.Graph.Connections));
        editor.Redo();
        Assert.Contains(read, editor.Graph.Nodes);
        Assert.Equal(new PinRef(read.Id, "line"), Assert.Single(editor.Graph.Connections).From);
    }

    [Fact]
    public void AddConnectedNodeRejectsIncompatibleType()
    {
        GraphEditor editor = NewEditor();
        GraphNode print = editor.AddNode("console.write-line", 0, 0);

        Assert.Throws<ArgumentException>(() => editor.AddConnectedNode("literal.int", 0, 0, new PinRef(print.Id, "text")));
        Assert.Single(editor.Graph.Nodes);
    }

    [Fact]
    public void CopyFragmentKeepsOnlyInternalConnectionsAndIsIndependent()
    {
        GraphEditor editor = NewEditor();
        GraphNode entry = editor.AddNode(BuiltInNodes.EntryTypeId, 0, 0);
        GraphNode print = editor.AddNode("console.write-line", 200, 0);
        GraphNode text = editor.AddNode("literal.string", 100, 100);
        editor.Connect(new PinRef(entry.Id, "then"), new PinRef(print.Id, "exec"));
        editor.Connect(new PinRef(text.Id, "value"), new PinRef(print.Id, "text"));

        GraphDocument fragment = editor.CopyFragment([print.Id, text.Id]);
        text.Properties["value"] = "changed";

        Assert.Equal(GraphEditor.FragmentId, fragment.Id);
        Assert.Equal([print.Id, text.Id], fragment.Nodes.Select(node => node.Id));
        Assert.Equal(new GraphConnection(new PinRef(text.Id, "value"), new PinRef(print.Id, "text")), Assert.Single(fragment.Connections));
        Assert.Equal("Hello, World!", fragment.Nodes[1].Properties["value"]);
    }

    [Fact]
    public void PasteCreatesNewIdsAtTargetAsOneUndoStep()
    {
        GraphEditor editor = NewEditor();
        GraphNode print = editor.AddNode("console.write-line", 200, 50);
        GraphNode text = editor.AddNode("literal.string", 100, 150);
        editor.Connect(new PinRef(text.Id, "value"), new PinRef(print.Id, "text"));

        // Through the clipboard text format, as the IDE does.
        GraphDocument fragment = GraphSerializer.Deserialize(GraphSerializer.Serialize(editor.CopyFragment([print.Id, text.Id])));
        IReadOnlyList<GraphNode> pasted = editor.Paste(fragment, 1000, 2000).Nodes;

        Assert.Equal(2, pasted.Count);
        Assert.DoesNotContain(pasted, node => node.Id == print.Id || node.Id == text.Id);
        Assert.Equal((1100.0, 2000.0), (pasted[0].X, pasted[0].Y));
        Assert.Equal((1000.0, 2100.0), (pasted[1].X, pasted[1].Y));
        Assert.Contains(new GraphConnection(new PinRef(pasted[1].Id, "value"), new PinRef(pasted[0].Id, "text")), editor.Graph.Connections);
        Assert.Equal(4, editor.Graph.Nodes.Count);
        Assert.Equal("Paste 2 items", editor.UndoDescription);

        editor.Undo();
        Assert.Equal([print, text], editor.Graph.Nodes);
        Assert.Single(editor.Graph.Connections);
    }

    [Fact]
    public void PasteDropsConnectionsThatLeaveTheFragment()
    {
        GraphEditor editor = NewEditor();
        GraphDocument fragment = GraphDocument.CreateEmpty("Fragment", GraphKind.EntryPoint);
        fragment.Nodes.Add(new GraphNode { Id = "a", Type = "literal.int" });
        fragment.Nodes.Add(new GraphNode { Id = "a", Type = "literal.bool" });
        fragment.Connections.Add(new(new PinRef("a", "value"), new PinRef("missing", "value")));

        GraphNode pasted = Assert.Single(editor.Paste(fragment, 0, 0).Nodes);

        Assert.Equal("literal.int", pasted.Type);
        Assert.Empty(editor.Graph.Connections);
    }

    [Fact]
    public void CommentEditsAreUndoable()
    {
        GraphEditor editor = NewEditor();
        GraphComment comment = editor.AddComment(10, 20, 300, 200);
        Assert.Equal("Comment", comment.Text);

        editor.SetCommentText(comment.Id, "Input");
        Assert.Equal("Edit comment", editor.UndoDescription);

        // Resizing is applied live by the view, then recorded.
        (comment.Width, comment.Height) = (500, 250);
        editor.CommitResize(comment.Id, 300, 200);

        editor.Undo();
        Assert.Equal((300.0, 200.0), (comment.Width, comment.Height));
        editor.Undo();
        Assert.Equal("Comment", comment.Text);
        editor.Undo();
        Assert.Empty(editor.Graph.Comments);
        editor.Redo();
        editor.Redo();
        editor.Redo();
        Assert.Equal(("Input", 500.0, 250.0), (comment.Text, comment.Width, comment.Height));
    }

    [Fact]
    public void MovingACommentAndItsNodesIsOneStep()
    {
        GraphEditor editor = NewEditor();
        GraphComment comment = editor.AddComment(0, 0, 300, 200);
        GraphNode node = editor.AddNode("literal.int", 20, 40);
        (comment.X, comment.Y, node.X, node.Y) = (100, 50, 120, 90);

        editor.CommitMove([new ItemMove(comment.Id, 0, 0, 100, 50), new ItemMove(node.Id, 20, 40, 120, 90)]);
        Assert.Equal("Move 2 items", editor.UndoDescription);

        editor.Undo();
        Assert.Equal((0.0, 0.0, 20.0, 40.0), (comment.X, comment.Y, node.X, node.Y));
    }

    [Fact]
    public void DeleteRemovesCommentsAndNodesAndUndoRestoresOrder()
    {
        GraphEditor editor = NewEditor();
        GraphComment first = editor.AddComment(0, 0, 200, 100);
        GraphComment second = editor.AddComment(0, 200, 200, 100);
        GraphNode node = editor.AddNode("literal.int", 0, 0);

        editor.Delete([first.Id, node.Id]);
        Assert.Equal([second], editor.Graph.Comments);
        Assert.Empty(editor.Graph.Nodes);
        Assert.Equal("Delete 2 items", editor.UndoDescription);

        editor.Undo();
        Assert.Equal([first, second], editor.Graph.Comments);
        Assert.Equal([node], editor.Graph.Nodes);
    }

    [Fact]
    public void CommentsAreCopiedAndPastedWithNodes()
    {
        GraphEditor editor = NewEditor();
        GraphComment comment = editor.AddComment(100, 100, 300, 200);
        editor.SetCommentText(comment.Id, "Group");
        GraphNode node = editor.AddNode("literal.int", 150, 160);

        GraphDocument fragment = GraphSerializer.Deserialize(GraphSerializer.Serialize(editor.CopyFragment([comment.Id, node.Id])));
        PasteResult pasted = editor.Paste(fragment, 1000, 1000);

        GraphComment copy = Assert.Single(pasted.Comments);
        Assert.NotEqual(comment.Id, copy.Id);
        Assert.Equal(("Group", 1000.0, 1000.0, 300.0, 200.0), (copy.Text, copy.X, copy.Y, copy.Width, copy.Height));
        Assert.Equal((1050.0, 1060.0), (pasted.Nodes[0].X, pasted.Nodes[0].Y));
        Assert.Equal(2, editor.Graph.Comments.Count);
        Assert.Equal([pasted.Nodes[0].Id, copy.Id], pasted.Ids);
    }

    [Fact]
    public void ReroutePointsAreUndoable()
    {
        GraphEditor editor = NewEditor();
        GraphNode print = editor.AddNode("console.write-line", 400, 0);
        GraphNode text = editor.AddNode("literal.string", 0, 0);
        editor.Connect(new PinRef(text.Id, "value"), new PinRef(print.Id, "text"));
        GraphConnection wire = Assert.Single(editor.Graph.Connections);

        editor.AddReroute(wire, 0, 200, 100);
        editor.AddReroute(wire, 0, 100, 50);
        Assert.Equal([new GraphPoint(100, 50), new GraphPoint(200, 100)], wire.Points);

        // Dragging is applied live by the view, then recorded.
        wire.Points![1] = new GraphPoint(250, 120);
        editor.CommitRerouteMove(wire, 1, 200, 100);
        editor.RemoveReroute(wire, 0);
        Assert.Equal([new GraphPoint(250, 120)], wire.Points);

        editor.Undo();
        Assert.Equal([new GraphPoint(100, 50), new GraphPoint(250, 120)], wire.Points);
        editor.Undo();
        Assert.Equal(new GraphPoint(200, 100), wire.Points![1]);
        editor.Undo();
        editor.Undo();
        Assert.Null(wire.Points);
    }

    [Fact]
    public void ConnectionIdentityIgnoresReroutePoints()
    {
        GraphEditor editor = NewEditor();
        GraphNode print = editor.AddNode("console.write-line", 400, 0);
        GraphNode text = editor.AddNode("literal.string", 0, 0);
        editor.Connect(new PinRef(text.Id, "value"), new PinRef(print.Id, "text"));
        editor.AddReroute(editor.Graph.Connections[0], 0, 200, 100);

        // Connecting the same pins again is a no-op, not a duplicate.
        editor.Connect(new PinRef(text.Id, "value"), new PinRef(print.Id, "text"));

        Assert.Single(editor.Graph.Connections);
        Assert.Equal(new GraphConnection(new PinRef(text.Id, "value"), new PinRef(print.Id, "text")), editor.Graph.Connections[0]);
    }

    [Fact]
    public void PasteMovesReroutePointsWithTheNodes()
    {
        GraphEditor editor = NewEditor();
        GraphNode print = editor.AddNode("console.write-line", 400, 0);
        GraphNode text = editor.AddNode("literal.string", 0, 0);
        editor.Connect(new PinRef(text.Id, "value"), new PinRef(print.Id, "text"));
        editor.AddReroute(editor.Graph.Connections[0], 0, 200, 100);

        GraphDocument fragment = GraphSerializer.Deserialize(GraphSerializer.Serialize(editor.CopyFragment([print.Id, text.Id])));
        PasteResult pasted = editor.Paste(fragment, 1000, 1000);

        GraphConnection copy = editor.Graph.Connections.Single(connection => connection.To.Node == pasted.Nodes[0].Id);
        Assert.Equal([new GraphPoint(1200, 1100)], copy.Points);
        Assert.Equal([new GraphPoint(200, 100)], editor.Graph.Connections[0].Points);
    }

    [Fact]
    public void PossibleConnectionsFollowTheRulesInBothDirections()
    {
        GraphEditor editor = NewEditor();
        GraphNode text = editor.AddNode("literal.string", 0, 0);
        GraphNode first = editor.AddNode("console.write-line", 300, 0);
        GraphNode second = editor.AddNode("console.write-line", 600, 0);

        // A literal has only a string output; Print Line takes a string and an execution input.
        Assert.Equal([new GraphConnection(new PinRef(text.Id, "value"), new PinRef(first.Id, "text"))], editor.PossibleConnections(text.Id, first.Id));

        // Execution can flow either way between two statements; existing wires are not offered again.
        Assert.Equal(
            [new GraphConnection(new PinRef(first.Id, "then"), new PinRef(second.Id, "exec")), new GraphConnection(new PinRef(second.Id, "then"), new PinRef(first.Id, "exec"))],
            editor.PossibleConnections(first.Id, second.Id));
        editor.Connect(new PinRef(first.Id, "then"), new PinRef(second.Id, "exec"));
        Assert.Equal([new GraphConnection(new PinRef(second.Id, "then"), new PinRef(first.Id, "exec"))], editor.PossibleConnections(second.Id, first.Id));
    }

    [Fact]
    public void DisconnectRemovesOneWireAndUndoRestoresItsPlace()
    {
        GraphEditor editor = NewEditor();
        GraphNode text = editor.AddNode("literal.string", 0, 0);
        GraphNode first = editor.AddNode("console.write-line", 300, 0);
        GraphNode second = editor.AddNode("console.write-line", 600, 0);
        editor.Connect(new PinRef(text.Id, "value"), new PinRef(first.Id, "text"));
        editor.Connect(new PinRef(text.Id, "value"), new PinRef(second.Id, "text"));
        GraphConnection[] before = [.. editor.Graph.Connections];

        editor.Disconnect(new GraphConnection(new PinRef(text.Id, "value"), new PinRef(first.Id, "text")));
        Assert.Equal([before[1]], editor.Graph.Connections);

        editor.Undo();
        Assert.Equal(before, editor.Graph.Connections);
    }

    [Fact]
    public void NewEditAfterUndoClearsRedo()
    {
        GraphEditor editor = NewEditor();
        editor.AddNode("literal.int", 0, 0);
        editor.Undo();
        editor.AddNode("literal.bool", 0, 0);
        Assert.False(editor.CanRedo);
    }
}
