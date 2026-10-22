using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using VisualStudioLikeIDE.App.Canvas;
using VisualStudioLikeIDE.Compiler.Nodes;
using VisualStudioLikeIDE.GraphModel;

namespace VisualStudioLikeIDE.App;

/// <summary>Node creation: the palette (search, double-click, drag onto the graph) and quick add after a wire is dropped on empty space.</summary>
public sealed partial class MainWindow
{
    private int placementOffset;
    private WireDroppedEventArgs? quickAddWire;
    private (ListBoxItem Item, Avalonia.Point Start)? palettePress;
    private bool paletteDragging;

    private void PopulatePalette()
    {
        PaletteList.Items.Clear();
        foreach (NodeDefinition definition in MatchingDefinitions(PaletteSearch.Text))
        {
            PaletteList.Items.Add(CreateNodeItem(definition));
        }
    }

    private IEnumerable<NodeDefinition> MatchingDefinitions(string? search)
    {
        string filter = search?.Trim() ?? "";
        // With a project open, the palette also lists a Get and a Set node for each of the graph's variables.
        return (workspace?.Editor.AvailableDefinitions ?? paletteLibrary.Definitions)
            .Where(definition => filter.Length == 0
                || definition.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || definition.Category.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || definition.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(definition => definition.Category, StringComparer.Ordinal)
            .ThenBy(definition => definition.DisplayName, StringComparer.Ordinal);
    }

    private static ListBoxItem CreateNodeItem(NodeDefinition definition)
    {
        var content = new StackPanel { Spacing = 1 };
        content.Children.Add(new TextBlock { Text = definition.DisplayName });
        content.Children.Add(new TextBlock { Text = definition.Category, FontSize = 11, Classes = { "secondary" } });
        var item = new ListBoxItem { Content = content, Tag = definition, Padding = new Avalonia.Thickness(10, 3) };
        ToolTip.SetTip(item, definition.Description);
        return item;
    }

    private void AddNodeFromPalette(ListBoxItem? item)
    {
        if (workspace is null)
        {
            SetStatus("Create or open a project first.");
            return;
        }

        if (item?.Tag is NodeDefinition definition)
        {
            AddNodeAtViewCenter(definition);
        }
    }

    private void AddNodeAtViewCenter(NodeDefinition definition)
    {
        if (workspace is null)
        {
            return;
        }

        // Stagger nodes added in a row so they do not stack exactly on top of each other.
        Avalonia.Point center = GraphCanvas.ViewCenterWorld;
        double offset = (placementOffset++ % 6) * 24;
        GraphNode node = workspace.Editor.AddNode(definition, Math.Round(center.X - 80 + offset), Math.Round(center.Y - 40 + offset));
        GraphCanvas.Select(node.Id, frame: false);
        GraphCanvas.Focus();
    }

    private void OnPalettePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        palettePress = workspace is not null
            && e.GetCurrentPoint(PaletteList).Properties.IsLeftButtonPressed
            && (e.Source as Avalonia.Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { } item
            ? (item, e.GetPosition(this))
            : null;
    }

    private void OnPalettePointerMoved(object? sender, PointerEventArgs e)
    {
        if (palettePress is not { } press || paletteDragging || Avalonia.Point.Distance(e.GetPosition(this), press.Start) < 6)
        {
            return;
        }

        // The drag stays inside this window, so pointer capture is enough; no OS drag-and-drop data is involved.
        paletteDragging = true;
        e.Pointer.Capture(PaletteList);
        Cursor = new Cursor(StandardCursorType.DragCopy);
        SetStatus("Release over the graph to add the node.");
    }

    private void OnPalettePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        bool dragging = paletteDragging;
        ListBoxItem? item = palettePress?.Item;
        palettePress = null;
        paletteDragging = false;
        if (!dragging)
        {
            return;
        }

        e.Pointer.Capture(null);
        Cursor = Cursor.Default;
        Avalonia.Point position = e.GetPosition(GraphCanvas);
        if (workspace is null || item?.Tag is not NodeDefinition definition || !new Avalonia.Rect(GraphCanvas.Bounds.Size).Contains(position))
        {
            SetStatus("");
            return;
        }

        Avalonia.Point world = GraphCanvas.ToWorld(position);
        GraphNode node = workspace.Editor.AddNode(definition, Math.Round(world.X - 20), Math.Round(world.Y - 14));
        GraphCanvas.Select(node.Id, frame: false);
        GraphCanvas.Focus();
        SetStatus($"Added {definition.DisplayName}.");
        e.Handled = true;
    }

    private void ShowQuickAdd(WireDroppedEventArgs wire)
    {
        if (workspace?.Editor.FindPin(wire.Source) is not { } pin)
        {
            return;
        }

        quickAddWire = wire;
        string type = pin.DataType?.DisplayName ?? "execution";
        QuickAddTitle.Text = pin.Direction == PinDirection.Output ? $"Connect {type} output to…" : $"Connect {type} input from…";
        QuickAddSearch.Text = "";
        PopulateQuickAdd();

        // Keep the popup inside the canvas.
        double x = Math.Clamp(wire.Screen.X, 0, Math.Max(0, GraphCanvas.Bounds.Width - QuickAdd.Width));
        double y = Math.Clamp(wire.Screen.Y, 0, Math.Max(0, GraphCanvas.Bounds.Height - QuickAdd.MaxHeight));
        QuickAdd.Margin = new Avalonia.Thickness(x, y, 0, 0);
        QuickAdd.IsVisible = true;
        QuickAddSearch.Focus();
    }

    private void PopulateQuickAdd()
    {
        QuickAddList.Items.Clear();
        if (workspace is null || quickAddWire is null)
        {
            return;
        }

        foreach (NodeDefinition definition in MatchingDefinitions(QuickAddSearch.Text)
            .Where(definition => workspace.Editor.FindCompatiblePin(definition, quickAddWire.Source) is not null))
        {
            QuickAddList.Items.Add(CreateNodeItem(definition));
        }
    }

    private void AddNodeFromQuickAdd(ListBoxItem? item)
    {
        if (workspace is null || quickAddWire is not { } wire || item?.Tag is not NodeDefinition definition)
        {
            return;
        }

        // Place the node so its connecting side sits at the release point.
        bool fromOutput = workspace.Editor.FindPin(wire.Source)?.Direction == PinDirection.Output;
        double x = fromOutput ? wire.World.X : wire.World.X - 180;
        GraphNode node = workspace.Editor.AddConnectedNode(definition, Math.Round(x), Math.Round(wire.World.Y - 14), wire.Source);
        HideQuickAdd();
        GraphCanvas.Select(node.Id, frame: false);
        GraphCanvas.Focus();
    }

    private void HideQuickAdd()
    {
        QuickAdd.IsVisible = false;
        quickAddWire = null;
    }
}
