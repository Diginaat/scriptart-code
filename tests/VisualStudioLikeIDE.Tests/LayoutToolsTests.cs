using VisualStudioLikeIDE.Compiler.Nodes;
using VisualStudioLikeIDE.GraphModel;
using VisualStudioLikeIDE.NodeEditor;

namespace VisualStudioLikeIDE.Tests;

public sealed class LayoutToolsTests
{
    private static readonly ItemBounds[] Items =
    [
        new("a", 0, 0, 100, 50),
        new("b", 300, 200, 200, 80),
        new("c", 150, 400, 120, 60),
    ];

    [Theory]
    [InlineData(Alignment.Left, 0, 0, 0)]
    [InlineData(Alignment.Right, 400, 300, 380)]
    [InlineData(Alignment.Center, 200, 150, 190)]
    public void AlignHorizontallyKeepsVerticalPositions(Alignment alignment, double a, double b, double c)
    {
        Dictionary<string, ItemMove> moves = LayoutTools.Align(Items, alignment).ToDictionary(move => move.Id);

        Assert.Equal([a, b, c], Items.Select(item => moves.TryGetValue(item.Id, out ItemMove move) ? move.ToX : item.X));
        Assert.All(moves.Values, move => Assert.Equal(move.FromY, move.ToY));
    }

    [Theory]
    [InlineData(Alignment.Top, 0, 0, 0)]
    [InlineData(Alignment.Bottom, 410, 380, 400)]
    [InlineData(Alignment.Middle, 205, 190, 200)]
    public void AlignVerticallyKeepsHorizontalPositions(Alignment alignment, double a, double b, double c)
    {
        Dictionary<string, ItemMove> moves = LayoutTools.Align(Items, alignment).ToDictionary(move => move.Id);

        Assert.Equal([a, b, c], Items.Select(item => moves.TryGetValue(item.Id, out ItemMove move) ? move.ToY : item.Y));
        Assert.All(moves.Values, move => Assert.Equal(move.FromX, move.ToX));
    }

    [Fact]
    public void AlignLeavesOutItemsThatDoNotMove()
    {
        IReadOnlyList<ItemMove> moves = LayoutTools.Align(Items, Alignment.Left);

        Assert.DoesNotContain(moves, move => move.Id == "a");
        Assert.Empty(LayoutTools.Align([Items[0]], Alignment.Left));
    }

    [Fact]
    public void DistributeEqualizesGapsAndKeepsTheEnds()
    {
        // Horizontal order is a (0..100), c (150..270), b (300..500): total span 500, widths 420, so each gap is 40.
        Dictionary<string, ItemMove> moves = LayoutTools.Distribute(Items, Axis.Horizontal).ToDictionary(move => move.Id);

        Assert.Equal(["c"], moves.Keys);
        Assert.Equal(140, moves["c"].ToX);
        Assert.Equal(400, moves["c"].ToY);
    }

    [Fact]
    public void DistributeVerticallyUsesHeights()
    {
        // a (0..50), b (200..280), c (400..460): span 460, heights 190, gap 135.
        Dictionary<string, ItemMove> moves = LayoutTools.Distribute(Items, Axis.Vertical).ToDictionary(move => move.Id);

        Assert.Equal(185, moves["b"].ToY);
        Assert.False(moves.ContainsKey("a"));
        Assert.False(moves.ContainsKey("c"));
        Assert.Empty(LayoutTools.Distribute(Items[..2], Axis.Vertical));
    }

    [Fact]
    public void ArrangingIsOneUndoStep()
    {
        var editor = new GraphEditor(GraphDocument.CreateEmpty("Program", GraphKind.EntryPoint), NodeLibrary.CreateDefault());
        GraphNode first = editor.AddNode("literal.string", 0, 0);
        GraphNode second = editor.AddNode("literal.string", 300, 120);

        editor.MoveItems(LayoutTools.Align([new(first.Id, 0, 0, 100, 50), new(second.Id, 300, 120, 100, 50)], Alignment.Top));
        Assert.Equal(0, second.Y);

        editor.Undo();
        Assert.Equal(120, second.Y);
        Assert.Equal(0, first.Y);
    }
}
