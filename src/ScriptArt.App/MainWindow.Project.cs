using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using ScriptArt.App.Canvas;
using ScriptArt.Compiler.Nodes;
using ScriptArt.Compiler.TypeSystem;
using ScriptArt.GraphModel;
using ScriptArt.ProjectSystem;

namespace ScriptArt.App;

/// <summary>
/// The My Project panel: collapsible sections for the program graph, functions, classes and structs (each followed by its methods),
/// and the open graph's variables, each with a + button. Clicking a graph opens it; clicking a variable shows it in Details; dragging a variable onto the graph
/// adds its Get node (Ctrl) or Set node (Alt), or asks which one. Double-clicking a row, F2, or its right-click menu renames it; the
/// menu and Delete also remove it.
/// </summary>
public partial class MainWindow
{
    private readonly HashSet<string> collapsedSections = new(StringComparer.Ordinal);

    // The variable selected in My Project, shown in Details while no node is selected.
    private string? selectedVariableId;

    // The graph or variable whose row shows a name box. Kept by id so the box survives the panel being rebuilt.
    private string? renamingItemId;

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
        AddSection("Functions", OnAddFunction, [.. Graphs(GraphKind.Function).Where(graph => graph.Graph.Owner is null).Select(graph => GraphRow(graph, "ƒ"))]);
        IEnumerable<Control> TypeRows(GraphKind kind, string icon) => workspace.Project.Graphs.Where(graph => graph.Graph.Kind == kind).SelectMany(type =>
        {
            Control[] methods = [.. workspace.Project.Graphs
                .Where(member => member.Graph.Kind is GraphKind.Function or GraphKind.Constructor && member.Graph.Owner == type.Graph.Id && Matches(member.Graph.Name))
                .Select(member => GraphRow(member, member.Graph.Kind == GraphKind.Constructor ? "✦" : "ƒ", indent: 1))];
            return Matches(type.Graph.Name) || methods.Length > 0 ? methods.Prepend(GraphRow(type, icon)) : [];
        });
        AddSection("Classes", OnAddClass, [.. TypeRows(GraphKind.Class, "◆")]);
        AddSection("Structs", OnAddStruct, [.. TypeRows(GraphKind.Struct, "◇")]);
        AddFileSections(Matches);
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

    private Border Row(Control content, bool selected, string? tip, int indent = 0) =>
        new()
        {
            Child = content,
            Padding = new Thickness(18 + (indent * 16), 4, 8, 4),
            Background = selected ? (IBrush)this.FindResource("IdeAccent")! : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Focusable = true,
            [ToolTip.TipProperty] = tip,
        };

    private Border GraphRow(ProjectGraph graph, string icon, int indent = 0)
    {
        bool open = graph == workspace!.Graph;
        GraphDocument document = graph.Graph;
        Control content = renamingItemId == document.Id
            ? NameBox(document.Name, name => workspace?.RenameGraph(Current(document) ?? graph, name), $"{icon}  ")
            : new TextBlock { Text = $"{icon}  {document.Name}" };
        Border row = Row(content, open && selectedVariableId is null, $"{graph.RelativePath}\nDouble-click or F2 to rename; right-click for more.", indent);
        row.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed || workspace is null || renamingItemId == document.Id)
            {
                return;
            }

            if (e.ClickCount == 2)
            {
                BeginRename(document.Id);
                return;
            }

            if (Current(document) is { } current && current != workspace.Graph)
            {
                OpenProjectGraph(current);
            }
            else if (selectedVariableId is not null)
            {
                SelectVariable(null);
            }

            row.Focus();
        };
        row.KeyDown += (_, e) =>
        {
            if (e.Key == Key.F2)
            {
                BeginRename(document.Id);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && document.Kind != GraphKind.EntryPoint)
            {
                ConfirmRemoveGraph(row, document);
                e.Handled = true;
            }
        };
        row.ContextMenu = GraphMenu(row, document);
        return row;
    }

    // The project entry for a graph: saving replaces entries of renamed graphs, so rows look theirs up when used.
    private ProjectGraph? Current(GraphDocument document) => workspace?.Project.Graphs.FirstOrDefault(graph => graph.Graph == document);

    private void OpenProjectGraph(ProjectGraph graph)
    {
        recompileTimer.Stop();
        selectedVariableId = null;
        workspace!.OpenGraph(graph);
        ShowOpenGraph();
        ShowGeneratedCode();
        SetStatus($"Opened {graph.Graph.Name}.");
    }

    private ContextMenu GraphMenu(Border row, GraphDocument document)
    {
        List<MenuItem> items =
        [
            Item("Open", () =>
            {
                if (Current(document) is { } graph)
                {
                    OpenProjectGraph(graph);
                }
            }),
            Item("Rename", () => BeginRename(document.Id), "F2"),
        ];
        if (TypeRef.IsTypeGraph(document))
        {
            items.Add(Item("Add Method", () => AddMember(document, constructor: false)));
            items.Add(Item("Add Constructor", () => AddMember(document, constructor: true)));
        }

        if (document.Kind != GraphKind.EntryPoint)
        {
            items.Add(Item($"Delete {KindName(document)}…", () => ConfirmRemoveGraph(row, document), "Delete"));
        }

        return new ContextMenu { ItemsSource = items };
    }

    private static MenuItem Item(string header, Action action, string? gesture = null)
    {
        var item = new MenuItem { Header = header, InputGesture = gesture is null ? null : KeyGesture.Parse(gesture) };
        item.Click += (_, _) => action();
        return item;
    }

    private static string KindName(GraphDocument document) => document switch
    {
        { Kind: GraphKind.Constructor } => "constructor",
        { Kind: GraphKind.Function, Owner: not null } => "method",
        { Kind: GraphKind.EntryPoint } => "graph",
        _ => document.Kind.ToString().ToLowerInvariant(),
    };

    private void AddMember(GraphDocument type, bool constructor)
    {
        if (workspace is null || Current(type) is not { } owner)
        {
            return;
        }

        ProjectGraph added = constructor ? workspace.AddConstructor(owner) : workspace.AddMethod(owner);
        ShowOpenGraph();
        SetStatus($"Added {(constructor ? "constructor" : "method")} {added.Graph.Name} to {type.Name}. Rename it here or in Details.");
    }

    private void BeginRename(string itemId)
    {
        renamingItemId = itemId;
        RefreshProjectPanel();
    }

    /// <summary>
    /// A name box for a row: Enter or leaving it applies a valid C# name, Esc cancels. An invalid name keeps the box open with
    /// the reason in the status bar.
    /// </summary>
    private Control NameBox(string current, Action<string> apply, string prefix = "")
    {
        var box = new TextBox { Text = current, MinWidth = 0, Padding = new Thickness(4, 1), FontSize = 13 };
        bool done = false;
        void Finish(bool commit)
        {
            string name = (box.Text ?? "").Trim();
            if (done)
            {
                return;
            }

            if (commit && name != current && !Compiler.GraphCompiler.IsValidIdentifier(name))
            {
                box.BorderBrush = (IBrush)this.FindResource("IdeError")!;
                SetStatus($"'{name}' is not a valid C# name. Use letters, digits, and underscores, starting with a letter. Esc cancels.");
                return;
            }

            done = true;
            renamingItemId = null;
            if (commit && name != current)
            {
                apply(name);
            }

            RefreshProjectPanel();
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                Finish(commit: e.Key == Key.Enter);
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => Finish(commit: true);
        box.AttachedToVisualTree += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            box.SelectAll();
        });
        if (prefix.Length == 0)
        {
            return box;
        }

        var panel = new DockPanel();
        panel.Children.Add(new TextBlock { Text = prefix, VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Left });
        panel.Children.Add(box);
        return panel;
    }

    private void ConfirmRemoveGraph(Control anchor, GraphDocument document)
    {
        if (workspace is null || Current(document) is not { } graph)
        {
            return;
        }

        int members = workspace.Project.Graphs.Count(other => other.Graph.Owner is not null && other.Graph.Owner == document.Id);
        string extra = members == 0 ? "" : $" and its {members} method{(members == 1 ? "" : "s")} or constructor{(members == 1 ? "" : "s")}";
        var delete = new Button { Content = "Delete" };
        var cancel = new Button { Content = "Cancel" };
        var flyout = new Flyout
        {
            Placement = PlacementMode.Right,
            Content = new StackPanel
            {
                Spacing = 8,
                MaxWidth = 320,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Delete {KindName(document)} {document.Name}{extra}? The file is deleted when you save, and this cannot be undone. Nodes that use it show as unresolved.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { delete, cancel } },
                },
            },
        };
        delete.Click += (_, _) =>
        {
            flyout.Hide();
            IReadOnlyList<ProjectGraph> removed = workspace.RemoveGraph(graph);
            ShowOpenGraph();
            SetStatus($"Deleted {string.Join(", ", removed.Select(item => item.Graph.Name))}. Save to delete the files.");
        };
        cancel.Click += (_, _) => flyout.Hide();
        flyout.ShowAt(anchor);
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
        content.Children.Add(renamingItemId == id
            ? NameBox(variable.Name, name => workspace?.Editor.RenameVariable(id, name))
            : new TextBlock { Text = variable.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });

        Border row = Row(content, selectedVariableId == id, "Drag onto the graph: Ctrl adds Get, Alt adds Set. Double-click or F2 to rename; right-click for more.");
        row.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed || renamingItemId == id)
            {
                return;
            }

            if (e.ClickCount == 2)
            {
                variablePress = null;
                BeginRename(id);
                return;
            }

            variablePress = (id, e.GetPosition(this));
            if (selectedVariableId != id)
            {
                SelectVariable(id);
            }

            row.Focus();
        };
        row.KeyDown += (_, e) =>
        {
            if (e.Key == Key.F2)
            {
                BeginRename(id);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete)
            {
                DeleteVariable(id);
                e.Handled = true;
            }
        };
        row.ContextMenu = new ContextMenu
        {
            ItemsSource = new[] { Item("Rename", () => BeginRename(id), "F2"), Item("Delete", () => DeleteVariable(id), "Delete") },
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

    /// <summary>
    /// A double-clicked node: a Call node, or a New node that runs a constructor, opens that graph; a variable node and Function
    /// Entry rename the variable or the graph in My Project.
    /// </summary>
    private void ActivateNode(string nodeId)
    {
        if (workspace?.Editor.Graph.FindNode(nodeId) is not { } node)
        {
            return;
        }

        string? target = node.Type switch
        {
            FunctionNodes.CallTypeId => node.Properties.GetValueOrDefault(FunctionNodes.FunctionProperty),
            TypeNodes.CreateTypeId => node.Properties.GetValueOrDefault(TypeNodes.ConstructorProperty),
            _ => null,
        };
        if (target is not null && workspace.Project.Graphs.FirstOrDefault(graph => graph.Graph.Id == target) is { } opened)
        {
            OpenProjectGraph(opened);
            return;
        }

        string? renamed = node.Type switch
        {
            VariableNodes.GetTypeId or VariableNodes.SetTypeId => node.Properties.GetValueOrDefault(VariableNodes.VariableProperty),
            FunctionNodes.EntryTypeId => workspace.Editor.Graph.Id,
            _ => null,
        };
        if (renamed is not null)
        {
            LeftTabs.SelectedIndex = 0;
            if (renamed != workspace.Editor.Graph.Id)
            {
                SelectVariable(renamed);
            }

            BeginRename(renamed);
        }
    }

    private void DeleteVariable(string variableId)
    {
        if (workspace?.Editor.Graph.FindVariable(variableId) is not { } variable)
        {
            return;
        }

        int uses = workspace.Editor.NodesUsing(variableId).Count;
        workspace.Editor.DeleteVariable(variableId);
        selectedVariableId = null;
        RefreshProjectPanel();
        ShowInspector();
        SetStatus($"Deleted variable {variable.Name}{(uses > 0 ? $" and its {uses} node{(uses == 1 ? "" : "s")}" : "")}. Undo restores it.");
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
