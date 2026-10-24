using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;

namespace ScriptArt.UiTests;

/// <summary>The action menu adds nodes from the keyboard as well as with the mouse.</summary>
public sealed class ActionMenuTests
{
    [AvaloniaFact]
    public void EnterInTheSearchBoxAddsTheFirstMatch()
    {
        using var session = new IdeSession();
        int before = OpenMenu(session);
        session.Type("branc");

        session.Press(Key.Enter);

        AssertAdded(session, before);
    }

    [AvaloniaFact]
    public void EnterOnAHighlightedNodeAddsIt()
    {
        using var session = new IdeSession();
        int before = OpenMenu(session);
        session.Type("branc");
        session.Press(Key.Down);
        Assert.Equal("Branch", (session.Window.ActionTree.SelectedItem as TreeViewItem)?.Header);

        session.Press(Key.Enter);

        AssertAdded(session, before);
    }

    // Right-clicks empty space on the canvas of the HelloWorld sample and returns the node count before adding.
    private static int OpenMenu(IdeSession session)
    {
        session.OpenSample("HelloWorld");
        NodeCanvasHelpers.ClickEmptySpace(session, MouseButton.Right);
        Assert.True(session.Window.ActionMenu.IsVisible);
        return session.Window.GraphCanvas.Editor!.Graph.Nodes.Count;
    }

    private static void AssertAdded(IdeSession session, int before)
    {
        Assert.False(session.Window.ActionMenu.IsVisible);
        Assert.Equal(before + 1, session.Window.GraphCanvas.Editor!.Graph.Nodes.Count);
    }

    private static class NodeCanvasHelpers
    {
        public static void ClickEmptySpace(IdeSession session, MouseButton button)
        {
            var canvas = session.Window.GraphCanvas;
            Point at = canvas.TranslatePoint(new Point(canvas.Bounds.Width - 40, canvas.Bounds.Height - 40), session.Window)!.Value;
            session.Window.MouseDown(at, button);
            session.Window.MouseUp(at, button);
            IdeSession.Settle();
        }
    }
}
