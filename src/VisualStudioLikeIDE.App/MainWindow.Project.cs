using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using VisualStudioLikeIDE.App.Canvas;
using VisualStudioLikeIDE.Compiler.Nodes;
using VisualStudioLikeIDE.Compiler.TypeSystem;
using VisualStudioLikeIDE.GraphModel;
using VisualStudioLikeIDE.ProjectSystem;

namespace VisualStudioLikeIDE.App;

/// <summary>
/// The My Project panel: collapsible sections for the program graph, functions, classes, structs, and the open graph's variables,
/// each with a + button. Clicking a graph opens it; clicking a variable shows it in Details; dragging a variable onto the graph
/// adds its Get node (Ctrl) or Set node (Alt), or asks which one.
/// </summary>
public partial class MainWindow
{
    private readonly HashSet<string> collapsedSections = new(StringComparer.Ordinal);

    // The variable selected in My Project, shown in Details while no node is selected.
    private string? selectedVariableId;

    private (string VariableId, Point Start)? variablePress;
    private bool variableDragging;

    private void OnAddVariable(object? sender, RoutedEventArgs e)
    {
        if (workspace is null || TypeRef.IsTypeGraph(workspace.Editor.Graph))
        {
            SetStatus("A class or struct has properties instead of variables; add them in Details.");
            return;
        }

        GraphVariable variable = workspace.Editor.AddVariable("int");
        focusVariableId = variable.Id;
        SelectVariable(variable.Id);
    }

    private void SelectVariable(string? variableId)
    {
        selectedVariableId = variableId;
        if (variableId is not null && GraphCanvas.SelectedItems.Count > 0)
        {
            GraphCanvas.SelectItems([]);
        }

        RefreshProjectPanel();
        ShowInspector();
    }

    private void RefreshProjectPanel()
    {
        ProjectPanel.Children.Clear();
        if (workspace is null)
        {
            return;
        }

        if (selectedVariableId is not null && workspace.Editor.Graph.FindVariable(selectedVariableId) is null)
        {
            selectedVariableId = null;
        }

        string filter = ProjectSearch.Text?.Trim() ?? "";
        bool Matches(string name) => filter.Length == 0 || name.Contains(filter, StringComparison.OrdinalIgnoreCase);
        IEnumerable<ProjectGraph> Graphs(GraphKind kind) => workspace.Project.Graphs.Where(graph => graph.Graph.Kind == kind && Matches(graph.Graph.Name));

        AddSection("Graphs", null, [.. Graphs(GraphKind.EntryPoint).Select(graph => GraphRow(graph, "▶"))]);
        AddSection("Functions", OnAddFunction, [.. Graphs(GraphKind.Function).Select(graph => GraphRow(graph, "ƒ"))]);
        AddSection("Classes", OnAddClass, [.. Graphs(GraphKind.Class).Select(graph => GraphRow(graph, "◆"))]);
        AddSection("Structs", OnAddStruct, [.. Graphs(GraphKind.Struct).Select(graph => GraphRow(graph, "◇"))]);
        if (!TypeRef.IsTypeGraph(workspace.Editor.Graph))
        {
            AddSection($"Variables of {workspace.Editor.Graph.Name}", OnAddVariable,
                [.. workspace.Editor.Graph.Variables.Where(variable => Matches(variable.Name)).Select(VariableRow)]);
        }
    }

    private void AddSection(string title, EventHandler<RoutedEventArgs>? add, IReadOnlyList<Control> rows)
    {
        bool collapsed = collapsedSections.Contains(title);
        var header = new DockPanel { Background = (IBrush)this.FindResource("IdePanelHeaderBackground")!, Margin = new Thickness(0, 2, 0, 0) };
        if (add is not null)
        {
            var plus = new Button { Content = "+", Padding = new Thickness(8, 0), Margin = new Thickness(0, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(plus, $"Add to {title}");
            plus.Click += add;
            DockPanel.SetDock(plus, Dock.Right);
            header.Children.Add(plus);
        }

        header.Children.Add(new TextBlock
        {
            Text = $"{(collapsed ? "▸" : "▾")} {title}",
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            Margin = new Thickness(8, 5),
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.PointerPressed += (_, e) =>
        {
            if (e.Source is not Button && e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
            {
                if (!collapsedSections.Remove(title))
                {
                    collapsedSections.Add(title);
                }

                RefreshProjectPanel();
            }
        };
        ProjectPanel.Children.Add(header);
        if (!collapsed)
        {
            foreach (Control row in rows)
            {
                ProjectPanel.Children.Add(row);
            }
        }
    }

    private Border Row(Control content, bool selected, string? tip) =>
        new()
        {
            Child = content,
            Padding = new Thickness(18, 4, 8, 4),
            Background = selected ? (IBrush)this.FindResource("IdeAccent")! : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            [ToolTip.TipProperty] = tip,
        };

    private Border GraphRow(ProjectGraph graph, string icon)
    {
        bool open = graph == workspace!.Graph;
        Border row = Row(new TextBlock { Text = $"{icon}  {graph.Graph.Name}" }, open && selectedVariableId is null, graph.RelativePath);
        row.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed || workspace is null)
            {
                return;
            }

            if (graph != workspace.Graph)
            {
                recompileTimer.Stop();
                selectedVariableId = null;
                workspace.OpenGraph(graph);
                ShowOpenGraph();
                SetStatus($"Opened {graph.Graph.Name}.");
            }
            else if (selectedVariableId is not null)
            {
                SelectVariable(null);
            }
        };
        return row;
    }

    private Border VariableRow(GraphVariable variable)
    {
        string id = variable.Id;
        TypeRef? type = TypeRef.FromName(variable.Type, workspace!.Project.GraphDocuments);
        var content = new DockPanel();
        var typeText = new TextBlock { Text = variable.Type, Classes = { "secondary" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(typeText, Dock.Right);
        content.Children.Add(typeText);
        content.Children.Add(new Border
        {
            Width = 18,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = CanvasTheme.ForType(type ?? TypeRef.Void),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            [DockPanel.DockProperty] = Dock.Left,
        });
        content.Children.Add(new TextBlock { Text = variable.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });

        Border row = Row(content, selectedVariableId == id, "Drag onto the graph: Ctrl adds Get, Alt adds Set");
        row.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
            {
                variablePress = (id, e.GetPosition(this));
                if (selectedVariableId != id)
                {
                    SelectVariable(id);
                }
            }
        };
        row.PointerMoved += (_, e) =>
        {
            if (variablePress is not { } press || variableDragging || Point.Distance(e.GetPosition(this), press.Start) < 6)
            {
                return;
            }

            variableDragging = true;
            e.Pointer.Capture(row);
            Cursor = new Cursor(StandardCursorType.DragCopy);
            SetStatus("Release over the graph. Hold Ctrl for Get or Alt for Set.");
        };
        row.PointerReleased += (_, e) => DropVariable(e);
        return row;
    }

    private void DropVariable(PointerReleasedEventArgs e)
    {
        bool dragging = variableDragging;
        string? variableId = variablePress?.VariableId;
        variablePress = null;
        variableDragging = false;
        if (!dragging)
        {
            return;
        }

        e.Pointer.Capture(null);
        Cursor = Cursor.Default;
        SetStatus("");
        Point position = e.GetPosition(GraphCanvas);
        if (workspace is null || variableId is null || !new Rect(GraphCanvas.Bounds.Size).Contains(position))
        {
            return;
        }

        Point world = GraphCanvas.ToWorld(position);
        NodeDefinition? Definition(bool set) => workspace.Editor.AvailableDefinitions.FirstOrDefault(definition =>
            set ? definition is VariableSetNode setter && setter.VariableId == variableId : definition is VariableGetNode getter && getter.VariableId == variableId);
        void Add(bool set)
        {
            if (Definition(set) is { } definition)
            {
                GraphNode node = workspace.Editor.AddNode(definition, Math.Round(world.X - 20), Math.Round(world.Y - 14));
                GraphCanvas.Select(node.Id, frame: false);
                GraphCanvas.Focus();
            }
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            Add(set: e.KeyModifiers.HasFlag(KeyModifiers.Alt));
            return;
        }

        var get = new MenuItem { Header = Definition(false)?.DisplayName ?? "Get" };
        var set = new MenuItem { Header = Definition(true)?.DisplayName ?? "Set" };
        get.Click += (_, _) => Add(set: false);
        set.Click += (_, _) => Add(set: true);
        var menu = new ContextMenu { ItemsSource = new[] { get, set }, Placement = PlacementMode.Pointer };
        menu.Open(GraphCanvas);
    }
}
