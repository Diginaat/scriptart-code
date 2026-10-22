using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using VisualStudioLikeIDE.App.Canvas;
using VisualStudioLikeIDE.Compiler.Nodes;
using VisualStudioLikeIDE.Compiler.TypeSystem;
using VisualStudioLikeIDE.GraphModel;

namespace VisualStudioLikeIDE.App;

/// <summary>
/// Node creation: the palette (search, double-click, drag onto the graph) and the action menu, opened by right-clicking empty space
/// or releasing a wire there, with nodes grouped by category and an optional context-sensitive filter.
/// </summary>
public sealed partial class MainWindow
{
    private int placementOffset;
    private ActionMenuContext? actionMenu;
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

    /// <summary>Opens the action menu for a wire released on empty space; the new node is connected to the wire's pin.</summary>
    private void ShowActionMenu(WireDroppedEventArgs wire)
    {
        if (workspace?.Editor.FindPin(wire.Source) is not { } pin)
        {
            return;
        }

        string type = pin.DataType?.DisplayName ?? "execution";
        OpenActionMenu(new ActionMenuContext(wire.World, wire.Screen, [wire.Source],
            pin.Direction == PinDirection.Output ? $"Actions taking {type} from {pin.DisplayName}" : $"Actions giving {type} to {pin.DisplayName}"));
    }

    /// <summary>
    /// Opens the action menu for a right-click on empty space. With one node selected, context sensitivity offers nodes that can
    /// connect to one of its free pins, and the chosen node is wired to it.
    /// </summary>
    private void ShowActionMenu(ActionMenuRequestedEventArgs request)
    {
        if (workspace is null || TypeRef.IsTypeGraph(workspace.Editor.Graph))
        {
            return;
        }

        IReadOnlyList<PinRef> anchors = GraphCanvas.SelectedItems.Count == 1 ? workspace.Editor.FreePins(GraphCanvas.SelectedItems.First()) : [];
        string title = anchors.Count > 0 && workspace.Editor.Graph.FindNode(anchors[0].Node) is { } node && workspace.Editor.DefinitionOf(node) is { } definition
            ? $"Actions connecting to {definition.DisplayName}"
            : $"All Actions for {workspace.Editor.Graph.Name}";
        OpenActionMenu(new ActionMenuContext(request.World, request.Screen, anchors, title));
    }

    private void OpenActionMenu(ActionMenuContext context)
    {
        HideCommandPalette(focusGraph: false);
        actionMenu = context;
        ActionSearch.Text = "";
        PopulateActionMenu();

        // Keep the menu inside the canvas.
        double x = Math.Clamp(context.Screen.X, 0, Math.Max(0, GraphCanvas.Bounds.Width - ActionMenu.Width));
        double y = Math.Clamp(context.Screen.Y, 0, Math.Max(0, GraphCanvas.Bounds.Height - ActionMenu.Height));
        ActionMenu.Margin = new Avalonia.Thickness(x, y, 0, 0);
        ActionMenu.IsVisible = true;
        ActionSearch.Focus();
    }

    private void PopulateActionMenu()
    {
        ActionTree.Items.Clear();
        if (workspace is null || actionMenu is not { } context)
        {
            return;
        }

        bool sensitive = ContextSensitiveBox.IsChecked == true;
        ActionMenuTitle.Text = sensitive ? context.Title : $"All Actions for {workspace.Editor.Graph.Name}";
        HashSet<NodeDefinition> offered = [.. workspace.Editor.Actions(context.Anchors, sensitive)];
        string filter = ActionSearch.Text?.Trim() ?? "";
        List<IGrouping<string, NodeDefinition>> categories = [.. MatchingDefinitions(filter).Where(offered.Contains).GroupBy(definition => definition.Category)];

        // While searching, or when few nodes fit, every category is open so the matches show without extra clicks.
        bool expand = filter.Length > 0 || categories.Sum(category => category.Count()) <= 30;
        foreach (IGrouping<string, NodeDefinition> category in categories)
        {
            var header = new TreeViewItem
            {
                Header = new TextBlock { Text = category.Key, FontWeight = FontWeight.SemiBold },
                IsExpanded = expand,
                MinHeight = 0,
            };
            foreach (NodeDefinition definition in category)
            {
                var item = new TreeViewItem { Header = definition.DisplayName, Tag = definition, MinHeight = 0 };
                ToolTip.SetTip(item, definition.Description);
                header.Items.Add(item);
            }

            ActionTree.Items.Add(header);
        }

        if (categories.Count == 0)
        {
            ActionTree.Items.Add(new TreeViewItem
            {
                Header = new TextBlock
                {
                    Text = sensitive ? "No matching node fits here. Turn off Context Sensitive to see every node." : "No matching node.",
                    Classes = { "secondary" },
                    TextWrapping = TextWrapping.Wrap,
                },
                IsEnabled = false,
            });
        }
    }

    private TreeViewItem? FirstAction() =>
        ActionTree.Items.OfType<TreeViewItem>().SelectMany(category => category.Items.OfType<TreeViewItem>()).FirstOrDefault();

    private void AddNodeFromActionMenu(TreeViewItem? item)
    {
        if (workspace is null || actionMenu is not { } context || item?.Tag is not NodeDefinition definition)
        {
            return;
        }

        // Place the node so its connecting side sits at the click point: right of an output, left of an input.
        PinRef? anchor = context.Anchors.FirstOrDefault(anchor => workspace.Editor.FindCompatiblePin(definition, anchor) is not null);
        bool toInput = anchor is not null && workspace.Editor.FindPin(anchor)?.Direction == PinDirection.Input;
        double x = toInput ? context.World.X - 180 : context.World.X;
        GraphNode node = workspace.Editor.AddActionNode(definition, Math.Round(x), Math.Round(context.World.Y - 14), context.Anchors);
        HideActionMenu();
        GraphCanvas.Select(node.Id, frame: false);
        GraphCanvas.Focus();
        SetStatus($"Added {definition.DisplayName}.");
    }

    private void HideActionMenu()
    {
        ActionMenu.IsVisible = false;
        actionMenu = null;
    }

    /// <summary>Where the action menu was opened, and the pins a chosen node is connected to.</summary>
    private sealed record ActionMenuContext(Avalonia.Point World, Avalonia.Point Screen, IReadOnlyList<PinRef> Anchors, string Title);
}
