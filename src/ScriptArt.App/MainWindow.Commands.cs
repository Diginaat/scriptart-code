using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using ScriptArt.Compiler.Lowering;
using ScriptArt.Compiler.Nodes;
using ScriptArt.GraphModel;
using ScriptArt.NodeEditor;

namespace ScriptArt.App;

/// <summary>
/// The command palette: every enabled menu command and every node, searchable by name. The same overlay lists the
/// connections between two selected nodes, so pins can be connected from the keyboard.
/// </summary>
public partial class MainWindow
{
    private sealed record PaletteCommand(string Title, string? Detail, Action Run);

    private List<PaletteCommand> paletteCommands = [];

    private void ShowCommandPalette() =>
        ShowPalette([.. MenuCommands(MainMenu.Items.OfType<MenuItem>(), prefix: null), .. NodeCommands()], "Type a command or node name");

    private void ShowPalette(List<PaletteCommand> commands, string placeholder)
    {
        HideActionMenu();
        paletteCommands = commands;
        CommandSearch.PlaceholderText = placeholder;
        CommandSearch.Text = "";
        PopulateCommandPalette();
        CommandPalette.IsVisible = true;
        CommandSearch.Focus();
    }

    private void HideCommandPalette(bool focusGraph)
    {
        CommandPalette.IsVisible = false;
        paletteCommands = [];
        if (focusGraph)
        {
            GraphCanvas.Focus();
        }
    }

    private void PopulateCommandPalette()
    {
        CommandList.Items.Clear();
        string? search = CommandSearch.Text;
        foreach (PaletteCommand command in paletteCommands
            .Select((command, order) => (command, order, rank: SearchMatcher.Rank(command.Title, search)))
            .Where(match => match.rank is not null)
            .OrderBy(match => match.rank)
            .ThenBy(match => match.order)
            .Select(match => match.command))
        {
            var row = new DockPanel();
            if (command.Detail is not null)
            {
                var detail = new TextBlock { Text = command.Detail, Classes = { "secondary" }, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(detail, Dock.Right);
                row.Children.Add(detail);
            }

            row.Children.Add(new TextBlock { Text = command.Title, VerticalAlignment = VerticalAlignment.Center });
            CommandList.Items.Add(new ListBoxItem { Content = row, Tag = command });
        }

        CommandList.SelectedIndex = CommandList.ItemCount > 0 ? 0 : -1;
    }

    private void RunPaletteCommand(ListBoxItem? item)
    {
        if (item?.Tag is not PaletteCommand command)
        {
            return;
        }

        // Close first so commands that open dialogs or move focus start from the graph, as they would from the menu.
        HideCommandPalette(focusGraph: true);
        command.Run();
    }

    /// <summary>The menu's enabled leaf items, titled by their path ("Edit: Arrange › Align Left"). The menu stays the single list of commands.</summary>
    private IEnumerable<PaletteCommand> MenuCommands(IEnumerable<MenuItem> items, string? prefix)
    {
        foreach (MenuItem item in items)
        {
            if (item == CommandPaletteMenuItem || !item.IsEnabled || item.Header is not string header)
            {
                continue;
            }

            string name = header.Replace("_", "", StringComparison.Ordinal).TrimEnd('…');
            string title = prefix is null ? name : prefix.Contains(':', StringComparison.Ordinal) ? $"{prefix} › {name}" : $"{prefix}: {name}";
            MenuItem[] children = [.. item.Items.OfType<MenuItem>()];
            if (children.Length > 0)
            {
                foreach (PaletteCommand child in MenuCommands(children, title))
                {
                    yield return child;
                }

                continue;
            }

            string? detail = item.ToggleType == MenuItemToggleType.CheckBox ? (item.IsChecked ? "On" : "Off") : item.InputGesture?.ToString();
            yield return new PaletteCommand(title, detail, () =>
            {
                // Raising Click directly skips the menu's own check box toggle.
                if (item.ToggleType == MenuItemToggleType.CheckBox)
                {
                    item.IsChecked = !item.IsChecked;
                }

                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            });
        }
    }

    /// <summary>Lists every connection that can be made, and every wire that can be removed, between the two selected nodes.</summary>
    private void ShowConnectPicker()
    {
        if (workspace is null)
        {
            return;
        }

        GraphEditor editor = workspace.Editor;
        GraphNode[] nodes = [.. SelectedNodes().OrderBy(node => node.X).ThenBy(node => node.Y)];
        if (nodes.Length != 2)
        {
            SetStatus("Select exactly two nodes to connect them.");
            return;
        }

        string NodeName(string nodeId)
        {
            GraphNode node = nodes[0].Id == nodeId ? nodes[0] : nodes[1];
            string name = editor.DefinitionOf(node)?.DisplayName ?? node.Type;
            bool sameNames = editor.DefinitionOf(nodes[0])?.DisplayName == editor.DefinitionOf(nodes[1])?.DisplayName;
            return sameNames ? $"{name} ({(node == nodes[0] ? "left" : "right")})" : name;
        }

        string PinName(PinRef pin) =>
            $"{NodeName(pin.Node)} › {(editor.FindPin(pin)?.DisplayName is { Length: > 0 } name ? name : "Execution")}";

        List<PaletteCommand> commands = [];
        foreach (GraphConnection connection in editor.PossibleConnections(nodes[0].Id, nodes[1].Id))
        {
            PinDefinition from = editor.FindPin(connection.From)!;
            PinDefinition to = editor.FindPin(connection.To)!;
            bool replaces = (ConnectionRules.IsSingleConnection(to) && editor.Graph.Connections.Exists(existing => existing.To == connection.To))
                || (ConnectionRules.IsSingleConnection(from) && editor.Graph.Connections.Exists(existing => existing.From == connection.From));
            string type = from.DataType?.DisplayName ?? "execution";
            commands.Add(new PaletteCommand(
                $"Connect {PinName(connection.From)} → {PinName(connection.To)}",
                replaces ? $"{type}, replaces a wire" : type,
                () => editor.Connect(connection.From, connection.To)));
        }

        foreach (GraphConnection connection in editor.Graph.Connections
            .Where(connection => nodes.Any(node => node.Id == connection.From.Node) && nodes.Any(node => node.Id == connection.To.Node)))
        {
            commands.Add(new PaletteCommand($"Disconnect {PinName(connection.From)} → {PinName(connection.To)}", null, () => editor.Disconnect(connection)));
        }

        if (commands.Count == 0)
        {
            SetStatus("These two nodes have no pins that can be connected.");
            return;
        }

        ShowPalette(commands, "Filter connections");
    }

    private IEnumerable<GraphNode> SelectedNodes() =>
        workspace is null ? [] : GraphCanvas.SelectedItems.Select(workspace.Editor.Graph.FindNode).OfType<GraphNode>();

    private IEnumerable<PaletteCommand> NodeCommands() =>
        workspace is null
            ? []
            : MatchingDefinitions(null).Select(definition =>
                new PaletteCommand($"Add Node: {definition.DisplayName}", definition.Category, () => AddNodeAtViewCenter(definition)));
}
