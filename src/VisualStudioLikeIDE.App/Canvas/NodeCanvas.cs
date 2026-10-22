using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using VisualStudioLikeIDE.Compiler.Diagnostics;
using VisualStudioLikeIDE.Compiler.Lowering;
using VisualStudioLikeIDE.Compiler.Nodes;
using VisualStudioLikeIDE.GraphModel;
using VisualStudioLikeIDE.NodeEditor;

namespace VisualStudioLikeIDE.App.Canvas;

/// <summary>
/// The interactive graph surface: renders nodes and wires with Avalonia's immediate-mode drawing API
/// and turns pointer and keyboard input into <see cref="GraphEditor"/> operations.
/// </summary>
/// <remarks>
/// Controls: left-drag a node to move it (with the selection), left-drag from a pin to connect (releasing on empty space raises
/// <see cref="WireDropped"/> so the host can offer compatible nodes), left-drag on empty space to
/// box-select, right-click on empty space to open the action menu (<see cref="ActionMenuRequested"/>), right- or middle-drag to pan, wheel to zoom, Alt+click a pin to disconnect it, Delete to remove the selection,
/// Ctrl+A to select all, double-click a value or press F2 to edit it in place, C to add a comment around the selection
/// (drag its title to move it with the nodes inside, drag its corner to resize, double-click its title to rename),
/// double-click a wire to add a reroute point (drag it to move, Alt+click to remove), click or drag on the minimap to move the view, Home to frame the graph, arrow keys to cycle selection.
/// All state here is transient view state; the graph itself changes only through the editor.
/// </remarks>
internal sealed class NodeCanvas : Avalonia.Controls.Control
{
    private const double MinZoom = 0.2;
    private const double MaxZoom = 2.5;
    private const double DragThreshold = 4;

    private readonly Dictionary<string, NodeLayout> layouts = new(StringComparer.Ordinal);
    private readonly HashSet<string> selection = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Text, double Size, IBrush Brush, bool Bold), FormattedText> textCache = [];
    private readonly Dictionary<(IBrush Brush, double Thickness), IPen> pens = [];

    private GraphEditor? editor;
    private IReadOnlyDictionary<string, DiagnosticSeverity> nodeStates = new Dictionary<string, DiagnosticSeverity>();
    private Vector pan = new(80, 80);
    private double zoom = 1;

    private Gesture gesture;
    private Point pressScreen;
    private Point lastScreen;
    private bool dragMoved;
    private List<ItemMove> moveStart = [];
    private GraphComment? resizing;
    private (GraphConnection Connection, int Index, GraphPoint Start)? draggingReroute;
    private string? hoverMessage;
    private bool showMinimap = true;
    private bool showFrameStats;

    // Pins with at least one wire, rebuilt with the layouts; drawing checks every visible pin each frame.
    private readonly HashSet<PinRef> connectedPins = [];

    // The minimap's nodes as one geometry per category color, and their bounds; null when layouts have changed since.
    private (Rect Bounds, (IBrush Brush, Geometry Geometry)[] Groups)? minimapNodes;
    private readonly double[] frameTimes = new double[120];
    private int frameCount;
    private int drawnNodes;
    private int drawnWires;
    private MinimapMapping? minimapDrag;
    private Size resizeStart;
    private PinRef? connectFrom;
    private Point connectCurrentWorld;
    private (PinRef Pin, ConnectionCheck Check)? connectTarget;
    private PinRef? hoveredPin;

    public NodeCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    private enum Gesture
    {
        None,
        Panning,
        MovingNodes,
        Connecting,
        Marquee,
        Resizing,
        MovingReroute,
        Minimap,
    }

    public event EventHandler? SelectionChanged;

    /// <summary>Short contextual messages, such as why a connection is not allowed.</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>A wire was dragged from a pin and released on empty space; the host offers nodes to create there.</summary>
    public event EventHandler<WireDroppedEventArgs>? WireDropped;

    /// <summary>The user right-clicked empty space without dragging; the host offers nodes to create there.</summary>
    public event EventHandler<ActionMenuRequestedEventArgs>? ActionMenuRequested;

    /// <summary>The user asked to rename a comment (double-click on its title, F2, or right after creating it).</summary>
    public event EventHandler<CommentEditRequestedEventArgs>? CommentEditRequested;

    /// <summary>The user asked to edit a node's value in place (double-click on the value row, or F2).</summary>
    public event EventHandler<ValueEditRequestedEventArgs>? ValueEditRequested;

    /// <summary>Selected node and comment IDs.</summary>
    public IReadOnlyCollection<string> SelectedItems => selection;

    public GraphEditor? Editor
    {
        get => editor;
        set
        {
            if (editor is not null)
            {
                editor.Changed -= OnGraphChanged;
            }

            editor = value;
            selection.Clear();
            if (editor is not null)
            {
                editor.Changed += OnGraphChanged;
            }

            RebuildLayouts();
            FrameAll();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The most severe diagnostic per node, drawn as a colored outline.</summary>
    public IReadOnlyDictionary<string, DiagnosticSeverity> NodeStates
    {
        get => nodeStates;
        set
        {
            nodeStates = value;
            InvalidateVisual();
        }
    }

    /// <summary>Lines up the selected nodes and comments; does nothing for fewer than two.</summary>
    public void AlignSelection(Alignment alignment) => editor?.MoveItems(LayoutTools.Align(SelectedBounds(), alignment));

    /// <summary>Spaces the selected nodes and comments evenly; does nothing for fewer than three.</summary>
    public void DistributeSelection(Axis axis) => editor?.MoveItems(LayoutTools.Distribute(SelectedBounds(), axis));

    private ItemBounds[] SelectedBounds() =>
        [.. selection.Where(Exists).Select(id => BoundsOfItem(id) is { } bounds ? new ItemBounds(id, bounds.X, bounds.Y, bounds.Width, bounds.Height) : (ItemBounds?)null)
            .OfType<ItemBounds>()];

    private Rect? BoundsOfItem(string id) =>
        layouts.TryGetValue(id, out NodeLayout? layout) ? layout.Bounds
        : editor?.Graph.FindComment(id) is { } comment ? BoundsOf(comment)
        : null;

    /// <summary>
    /// Whether the time spent in <see cref="Render"/> is shown in the upper-left corner. This measures building the frame's
    /// drawing commands on the UI thread; rasterization happens later on Avalonia's render thread and is not included.
    /// </summary>
    public bool ShowFrameStats
    {
        get => showFrameStats;
        set
        {
            showFrameStats = value;
            frameCount = 0;
            InvalidateVisual();
        }
    }

    /// <summary>Whether the overview of the whole graph is drawn in the lower-right corner.</summary>
    public bool ShowMinimap
    {
        get => showMinimap;
        set
        {
            showMinimap = value;
            InvalidateVisual();
        }
    }

    /// <summary>The world-space point at the center of the visible area, where new nodes are placed.</summary>
    public Point ViewCenterWorld => ToWorld(new Point(Bounds.Width / 2, Bounds.Height / 2));

    /// <summary>The pointer position in graph coordinates while it is over the canvas, where pasted nodes are placed.</summary>
    public Point? PointerWorld => IsPointerOver ? ToWorld(lastScreen) : null;

    /// <summary>Converts a point in this control's coordinates to graph coordinates.</summary>
    public Point ToWorld(Point screen) => new((screen.X - pan.X) / zoom, (screen.Y - pan.Y) / zoom);

    public void Select(string itemId, bool frame)
    {
        Rect? found = layouts.TryGetValue(itemId, out NodeLayout? layout) ? layout.Bounds : editor?.Graph.FindComment(itemId) is { } comment ? BoundsOf(comment) : null;
        if (found is not { } bounds)
        {
            return;
        }

        selection.Clear();
        selection.Add(itemId);
        if (frame)
        {
            pan = new Vector((Bounds.Width / 2) - (bounds.Center.X * zoom), (Bounds.Height / 2) - (bounds.Center.Y * zoom));
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>Replaces the selection, for example with freshly pasted nodes.</summary>
    public void SelectItems(IEnumerable<string> itemIds)
    {
        selection.Clear();
        selection.UnionWith(itemIds.Where(Exists));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void SelectAll()
    {
        selection.UnionWith(layouts.Keys);
        selection.UnionWith(editor?.Graph.Comments.Select(comment => comment.Id) ?? []);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void DeleteSelection()
    {
        if (editor is not null && selection.Count > 0)
        {
            editor.Delete([.. selection]);
        }
    }

    /// <summary>Adds a comment around the selected nodes, or in the middle of the view, and asks for its title.</summary>
    public void AddComment()
    {
        if (editor is null)
        {
            return;
        }

        Rect[] selected = [.. selection.Where(layouts.ContainsKey).Select(id => layouts[id].Bounds)];
        Rect box = selected.Length > 0
            ? selected.Aggregate((a, b) => a.Union(b)).Inflate(new Thickness(24, CanvasTheme.CommentHeaderHeight + 12, 24, 24))
            : new Rect(ViewCenterWorld.X - 200, ViewCenterWorld.Y - 110, 400, 220);
        GraphComment comment = editor.AddComment(Math.Round(box.X), Math.Round(box.Y), Math.Round(box.Width), Math.Round(box.Height));
        Select(comment.Id, frame: false);
        RequestCommentEdit(comment);
    }

    /// <summary>Fits the whole graph in view.</summary>
    public void FrameAll()
    {
        if (layouts.Count == 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            zoom = 1;
            pan = new Vector(80, 80);
            InvalidateVisual();
            return;
        }

        Rect content = layouts.Values.Select(layout => layout.Bounds)
            .Concat(editor?.Graph.Comments.Select(BoundsOf) ?? [])
            .Aggregate((a, b) => a.Union(b)).Inflate(60);
        zoom = Math.Clamp(Math.Min(Bounds.Width / content.Width, Bounds.Height / content.Height), MinZoom, 1.25);
        pan = new Vector((Bounds.Width - (content.Width * zoom)) / 2 - (content.X * zoom), (Bounds.Height - (content.Height * zoom)) / 2 - (content.Y * zoom));
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var viewport = new Rect(Bounds.Size);
        context.FillRectangle(CanvasTheme.Background, viewport);
        if (editor is null)
        {
            return;
        }

        long renderStart = Stopwatch.GetTimestamp();
        drawnNodes = drawnWires = 0;
        Rect visibleWorld = new(ToWorld(viewport.TopLeft), ToWorld(viewport.BottomRight));
        using (context.PushTransform(Matrix.CreateScale(zoom, zoom) * Matrix.CreateTranslation(pan)))
        {
            DrawGrid(context, visibleWorld);
            foreach (GraphComment comment in editor.Graph.Comments)
            {
                if (BoundsOf(comment).Intersects(visibleWorld))
                {
                    DrawComment(context, comment);
                }
            }

            DrawConnections(context, visibleWorld);
            foreach (GraphNode node in editor.Graph.Nodes)
            {
                if (layouts.TryGetValue(node.Id, out NodeLayout? layout) && layout.Bounds.Intersects(visibleWorld))
                {
                    DrawNode(context, layout);
                    drawnNodes++;
                }
            }

            if (gesture == Gesture.Connecting && connectFrom is not null && FindPinLayout(connectFrom) is { } start)
            {
                IBrush brush = connectTarget is { Check.IsAllowed: false } ? CanvasTheme.Invalid : CanvasTheme.ForType(start.Pin.DataType);
                Point end = connectTarget is { } target && FindPinLayout(target.Pin) is { } targetPin ? targetPin.Center : connectCurrentWorld;
                bool fromOutput = start.Pin.Direction == PinDirection.Output;
                DrawWire(context, fromOutput ? start.Center : end, fromOutput ? end : start.Center, Pen(brush, 2.5));
            }

            if (gesture == Gesture.Marquee)
            {
                context.DrawRectangle(CanvasTheme.Marquee, CanvasTheme.MarqueeBorder, MarqueeWorld());
            }
        }

        DrawMinimap(context, visibleWorld);

        if (editor.Graph.Nodes.Count == 0 && !Compiler.TypeSystem.TypeRef.IsTypeGraph(editor.Graph))
        {
            FormattedText hint = Text("Empty graph. Right-click to add a node.", CanvasTheme.FontSize, CanvasTheme.TypeLabel);
            context.DrawText(hint, new Point((Bounds.Width - hint.Width) / 2, (Bounds.Height - hint.Height) / 2));
        }

        if (showFrameStats)
        {
            DrawFrameStats(context, Stopwatch.GetElapsedTime(renderStart).TotalMilliseconds);
        }
    }

    private void DrawFrameStats(DrawingContext context, double milliseconds)
    {
        frameTimes[frameCount % frameTimes.Length] = milliseconds;
        frameCount++;
        double[] recent = [.. frameTimes.Take(Math.Min(frameCount, frameTimes.Length)).Order()];
        string text = string.Create(CultureInfo.InvariantCulture,
            $"Render {milliseconds:F2} ms · median {recent[recent.Length / 2]:F2} · max {recent[^1]:F2} (last {recent.Length} frames)\n"
            + $"{drawnNodes:N0} of {editor!.Graph.Nodes.Count:N0} nodes, {drawnWires:N0} of {editor.Graph.Connections.Count:N0} wires drawn · zoom {zoom:P0}");

        // Not cached like other text: it changes every frame.
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, CanvasTheme.FontSize, CanvasTheme.Title);
        var frame = new Rect(8, 8, formatted.Width + 16, formatted.Height + 12);
        context.DrawRectangle(CanvasTheme.MinimapBackground, CanvasTheme.MinimapBorder, frame, 4, 4);
        context.DrawText(formatted, new Point(16, 14));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (editor is null)
        {
            return;
        }

        Focus();
        PointerPointProperties properties = e.GetCurrentPoint(this).Properties;
        pressScreen = lastScreen = e.GetPosition(this);
        dragMoved = false;
        e.Pointer.Capture(this);
        e.Handled = true;

        if (properties.IsRightButtonPressed || properties.IsMiddleButtonPressed)
        {
            gesture = Gesture.Panning;
            return;
        }

        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        // The minimap is drawn on top of everything, so it takes clicks first. Its mapping is frozen while dragging:
        // the overview includes the visible area, which moves with the drag.
        if (Minimap() is { } minimap && minimap.Frame.Contains(pressScreen))
        {
            gesture = Gesture.Minimap;
            minimapDrag = minimap;
            CenterOn(minimap.ToWorld(pressScreen));
            return;
        }

        Point world = ToWorld(pressScreen);
        if (HitPin(world) is { } pin)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                editor.DisconnectPin(pin);
                gesture = Gesture.None;
                return;
            }

            gesture = Gesture.Connecting;
            connectFrom = pin;
            connectCurrentWorld = world;
            connectTarget = null;
            return;
        }

        if (HitNode(world) is { } hit)
        {
            if (e.ClickCount == 2 && hit.PreviewBounds is { } valueRow && valueRow.Contains(world))
            {
                gesture = Gesture.None;
                e.Pointer.Capture(null);
                RequestValueEdit(hit);
                return;
            }

            SelectOnPress(hit.Node.Id, e.KeyModifiers);
            BeginMove();
            return;
        }

        if (HitCommentGrip(world) is { } grip)
        {
            gesture = Gesture.Resizing;
            resizing = grip;
            resizeStart = new Size(grip.Width, grip.Height);
            return;
        }

        if (HitCommentHeader(world) is { } comment)
        {
            if (e.ClickCount == 2)
            {
                gesture = Gesture.None;
                e.Pointer.Capture(null);
                RequestCommentEdit(comment);
                return;
            }

            SelectOnPress(comment.Id, e.KeyModifiers);
            BeginMove();
            return;
        }

        if (HitReroute(world) is { } knot)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                editor.RemoveReroute(knot.Connection, knot.Index);
                gesture = Gesture.None;
                return;
            }

            gesture = Gesture.MovingReroute;
            draggingReroute = (knot.Connection, knot.Index, knot.Connection.Points![knot.Index]);
            return;
        }

        if (e.ClickCount == 2 && HitWire(world) is { } wire)
        {
            editor.AddReroute(wire.Connection, wire.Segment, Math.Round(world.X), Math.Round(world.Y));

            // The button is still down, so the new point can be dragged into place right away.
            gesture = Gesture.MovingReroute;
            draggingReroute = (wire.Connection, wire.Segment, wire.Connection.Points![wire.Segment]);
            return;
        }

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && selection.Count > 0)
        {
            selection.Clear();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        gesture = Gesture.Marquee;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (editor is null)
        {
            return;
        }

        Point screen = e.GetPosition(this);
        Vector delta = screen - lastScreen;
        lastScreen = screen;
        dragMoved |= Point.Distance(screen, pressScreen) > DragThreshold;

        switch (gesture)
        {
            case Gesture.Panning:
                pan += delta;
                InvalidateVisual();
                break;

            case Gesture.Minimap when minimapDrag is { } minimap:
                CenterOn(minimap.ToWorld(screen));
                break;

            case Gesture.MovingNodes when dragMoved:
                Vector worldDelta = (screen - pressScreen) / zoom;
                foreach (ItemMove start in moveStart)
                {
                    double x = Math.Round(start.FromX + worldDelta.X);
                    double y = Math.Round(start.FromY + worldDelta.Y);
                    if (layouts.TryGetValue(start.Id, out NodeLayout? layout))
                    {
                        (layout.Node.X, layout.Node.Y) = (x, y);
                        layouts[start.Id] = NodeLayout.Create(layout.Node, layout.Definition, MeasureText);
                        minimapNodes = null;
                    }
                    else if (editor.Graph.FindComment(start.Id) is { } moved)
                    {
                        (moved.X, moved.Y) = (x, y);
                    }
                }

                InvalidateVisual();
                break;

            case Gesture.MovingReroute when draggingReroute is { } reroute:
                Vector rerouteDelta = (screen - pressScreen) / zoom;
                reroute.Connection.Points![reroute.Index] = new GraphPoint(Math.Round(reroute.Start.X + rerouteDelta.X), Math.Round(reroute.Start.Y + rerouteDelta.Y));
                InvalidateVisual();
                break;

            case Gesture.Resizing when resizing is not null:
                Vector resizeDelta = (screen - pressScreen) / zoom;
                resizing.Width = Math.Max(CanvasTheme.CommentMinWidth, Math.Round(resizeStart.Width + resizeDelta.X));
                resizing.Height = Math.Max(CanvasTheme.CommentMinHeight, Math.Round(resizeStart.Height + resizeDelta.Y));
                InvalidateVisual();
                break;

            case Gesture.Connecting:
                connectCurrentWorld = ToWorld(screen);
                UpdateConnectTarget();
                InvalidateVisual();
                break;

            case Gesture.Marquee:
                InvalidateVisual();
                break;

            case Gesture.None:
                UpdateHover(ToWorld(screen));
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (editor is null)
        {
            return;
        }

        e.Pointer.Capture(null);
        switch (gesture)
        {
            case Gesture.MovingNodes when dragMoved:
                editor.CommitMove([.. moveStart.Select(start => PositionOf(start.Id) is { } to ? start with { ToX = to.X, ToY = to.Y } : start)]);
                break;

            case Gesture.MovingReroute when draggingReroute is { } moved:
                editor.CommitRerouteMove(moved.Connection, moved.Index, moved.Start.X, moved.Start.Y);
                break;

            case Gesture.Resizing when resizing is not null:
                editor.CommitResize(resizing.Id, resizeStart.Width, resizeStart.Height);
                break;

            case Gesture.Connecting when connectTarget is { } target && connectFrom is not null:
                ConnectionCheck result = editor.Connect(connectFrom, target.Pin);
                if (!result.IsAllowed)
                {
                    StatusChanged?.Invoke(this, result.Error!);
                }

                // The pointer rests on the target pin; keep the hover from replacing the result message.
                hoveredPin = target.Pin;
                hoverMessage = PinMessage(target.Pin);
                break;

            case Gesture.Panning when !dragMoved && e.InitialPressMouseButton == MouseButton.Right && HitNode(ToWorld(lastScreen)) is null:
                ActionMenuRequested?.Invoke(this, new ActionMenuRequestedEventArgs(ToWorld(lastScreen), lastScreen));
                break;

            case Gesture.Connecting when connectFrom is not null && dragMoved && HitNode(connectCurrentWorld) is null:
                WireDropped?.Invoke(this, new WireDroppedEventArgs(connectFrom, connectCurrentWorld, lastScreen));
                break;

            case Gesture.Marquee:
                Rect marquee = MarqueeWorld();
                foreach (NodeLayout layout in layouts.Values.Where(layout => marquee.Intersects(layout.Bounds)))
                {
                    selection.Add(layout.Node.Id);
                }

                // Comments are large; select them only when the box encloses them completely.
                selection.UnionWith(editor.Graph.Comments.Where(comment => marquee.Contains(BoundsOf(comment))).Select(comment => comment.Id));

                SelectionChanged?.Invoke(this, EventArgs.Empty);
                break;
        }

        gesture = Gesture.None;
        connectFrom = null;
        connectTarget = null;
        resizing = null;
        draggingReroute = null;
        minimapDrag = null;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Point screen = e.GetPosition(this);
        Point anchor = ToWorld(screen);
        zoom = Math.Clamp(zoom * Math.Pow(1.15, e.Delta.Y), MinZoom, MaxZoom);
        pan = new Vector(screen.X - (anchor.X * zoom), screen.Y - (anchor.Y * zoom));
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (editor is null || e.Handled)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Delete or Key.Back:
                DeleteSelection();
                e.Handled = true;
                break;
            case Key.A when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                SelectAll();
                e.Handled = true;
                break;
            case Key.F2 when selection.Count == 1 && layouts.TryGetValue(selection.First(), out NodeLayout? selected) && selected.PreviewBounds is not null:
                RequestValueEdit(selected);
                e.Handled = true;
                break;
            case Key.F2 when selection.Count == 1 && editor.Graph.FindComment(selection.First()) is { } selectedComment:
                RequestCommentEdit(selectedComment);
                e.Handled = true;
                break;
            case Key.C when e.KeyModifiers == KeyModifiers.None:
                AddComment();
                e.Handled = true;
                break;

            case Key.Home:
                FrameAll();
                e.Handled = true;
                break;
            case Key.Escape:
                selection.Clear();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                InvalidateVisual();
                e.Handled = true;
                break;
            case Key.Right or Key.Down or Key.Left or Key.Up:
                CycleSelection(e.Key is Key.Right or Key.Down ? 1 : -1);
                e.Handled = true;
                break;
        }
    }

    protected override void OnSizeChanged(Avalonia.Controls.SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (e.PreviousSize.Width <= 0 && layouts.Count > 0)
        {
            FrameAll();
        }
    }

    /// <summary>Keyboard navigation: moves the selection to the next node in graph order and brings it into view.</summary>
    private void CycleSelection(int direction)
    {
        if (editor is null || editor.Graph.Nodes.Count == 0)
        {
            return;
        }

        List<GraphNode> nodes = editor.Graph.Nodes;
        int current = selection.Count == 1 ? nodes.FindIndex(node => selection.Contains(node.Id)) : -1;
        int next = current < 0 ? 0 : (current + direction + nodes.Count) % nodes.Count;
        Select(nodes[next].Id, frame: true);
    }

    private void OnGraphChanged(object? sender, EventArgs e)
    {
        RebuildLayouts();
        if (selection.RemoveWhere(id => !Exists(id)) > 0)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        InvalidateVisual();
    }

    private void RebuildLayouts()
    {
        layouts.Clear();
        connectedPins.Clear();
        minimapNodes = null;
        if (textCache.Count > 4096)
        {
            textCache.Clear();
        }

        if (editor is null)
        {
            InvalidateVisual();
            return;
        }

        foreach (GraphNode node in editor.Graph.Nodes)
        {
            if (editor.DefinitionOf(node) is { } definition)
            {
                layouts[node.Id] = NodeLayout.Create(node, definition, MeasureText);
            }
        }

        foreach (GraphConnection connection in editor.Graph.Connections)
        {
            connectedPins.Add(connection.From);
            connectedPins.Add(connection.To);
        }

        InvalidateVisual();
    }

    private void RequestValueEdit(NodeLayout layout) =>
        ValueEditRequested?.Invoke(this, new ValueEditRequestedEventArgs(layout.Node.Id, layout.Definition.Properties[0], ToScreen(layout.PreviewBounds!.Value)));

    private void CenterOn(Point world)
    {
        pan = new Vector((Bounds.Width / 2) - (world.X * zoom), (Bounds.Height / 2) - (world.Y * zoom));
        InvalidateVisual();
    }

    /// <summary>
    /// The minimap's frame and scale, or null when it is hidden, the graph is empty, or the canvas is too small to fit it.
    /// It shows everything in the graph plus the visible area.
    /// </summary>
    private MinimapMapping? Minimap()
    {
        if (minimapDrag is { } frozen)
        {
            return frozen;
        }

        if (!showMinimap || editor is null || editor.Graph.Nodes.Count + editor.Graph.Comments.Count == 0
            || Bounds.Width < CanvasTheme.MinimapWidth * 2 || Bounds.Height < CanvasTheme.MinimapHeight * 2)
        {
            return null;
        }

        Rect visible = new(ToWorld(default), ToWorld(new Point(Bounds.Width, Bounds.Height)));
        Rect content = editor.Graph.Comments.Select(BoundsOf)
            .Aggregate(MinimapNodes().Bounds is { Width: > 0 } nodes ? visible.Union(nodes) : visible, (a, b) => a.Union(b))
            .Inflate(40);
        var frame = new Rect(
            Bounds.Width - CanvasTheme.MinimapWidth - CanvasTheme.MinimapMargin,
            Bounds.Height - CanvasTheme.MinimapHeight - CanvasTheme.MinimapMargin,
            CanvasTheme.MinimapWidth,
            CanvasTheme.MinimapHeight);
        double scale = Math.Min(frame.Width / content.Width, frame.Height / content.Height);
        var origin = new Point(
            frame.X + ((frame.Width - (content.Width * scale)) / 2) - (content.X * scale),
            frame.Y + ((frame.Height - (content.Height * scale)) / 2) - (content.Y * scale));
        return new MinimapMapping(frame, scale, origin);
    }

    private (Rect Bounds, (IBrush Brush, Geometry Geometry)[] Groups) MinimapNodes()
    {
        if (minimapNodes is { } cached)
        {
            return cached;
        }

        Rect bounds = layouts.Count == 0 ? default : layouts.Values.Select(layout => layout.Bounds).Aggregate((a, b) => a.Union(b));
        (IBrush, Geometry)[] groups = [.. layouts.Values
            .GroupBy(layout => CanvasTheme.ForCategory(layout.Definition.Category))
            .Select(group =>
            {
                var geometry = new StreamGeometry();
                using (StreamGeometryContext path = geometry.Open())
                {
                    foreach (NodeLayout layout in group)
                    {
                        Rect r = layout.Bounds;
                        path.BeginFigure(r.TopLeft, isFilled: true);
                        path.LineTo(r.TopRight);
                        path.LineTo(r.BottomRight);
                        path.LineTo(r.BottomLeft);
                        path.EndFigure(isClosed: true);
                    }
                }

                return (group.Key, (Geometry)geometry);
            })];
        minimapNodes = (bounds, groups);
        return (bounds, groups);
    }

    private void DrawMinimap(DrawingContext context, Rect visibleWorld)
    {
        if (Minimap() is not { } minimap)
        {
            return;
        }

        context.DrawRectangle(CanvasTheme.MinimapBackground, CanvasTheme.MinimapBorder, minimap.Frame, 4, 4);
        using (context.PushClip(minimap.Frame))
        {
            foreach (GraphComment comment in editor!.Graph.Comments)
            {
                context.FillRectangle(CanvasTheme.MinimapComment, minimap.ToScreen(BoundsOf(comment)));
            }

            using (context.PushTransform(Matrix.CreateScale(minimap.Scale, minimap.Scale) * Matrix.CreateTranslation(minimap.Origin.X, minimap.Origin.Y)))
            {
                foreach ((IBrush brush, Geometry geometry) in MinimapNodes().Groups)
                {
                    context.DrawGeometry(brush, null, geometry);
                }
            }

            context.DrawRectangle(null, CanvasTheme.MinimapView, minimap.ToScreen(visibleWorld));
        }
    }

    private void RequestCommentEdit(GraphComment comment)
    {
        Rect header = BoundsOf(comment);
        CommentEditRequested?.Invoke(this, new CommentEditRequestedEventArgs(comment.Id, ToScreen(header.WithHeight(CanvasTheme.CommentHeaderHeight))));
    }

    /// <summary>Applies the usual click selection rules: Ctrl toggles, Shift adds, a plain click on an unselected item selects only it.</summary>
    private void SelectOnPress(string id, KeyModifiers modifiers)
    {
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            if (!selection.Remove(id))
            {
                selection.Add(id);
            }
        }
        else if (!selection.Contains(id))
        {
            if (!modifiers.HasFlag(KeyModifiers.Shift))
            {
                selection.Clear();
            }

            selection.Add(id);
        }
    }

    /// <summary>Starts dragging the selection. Selected comments carry the nodes and comments that lie completely inside them.</summary>
    private void BeginMove()
    {
        var moving = new HashSet<string>(selection, StringComparer.Ordinal);
        foreach (GraphComment comment in editor!.Graph.Comments.Where(comment => selection.Contains(comment.Id)))
        {
            Rect bounds = BoundsOf(comment);
            moving.UnionWith(layouts.Values.Where(layout => bounds.Contains(layout.Bounds)).Select(layout => layout.Node.Id));
            moving.UnionWith(editor.Graph.Comments.Where(inner => inner != comment && bounds.Contains(BoundsOf(inner))).Select(inner => inner.Id));
        }

        gesture = Gesture.MovingNodes;
        moveStart = [.. moving.Select(id => PositionOf(id) is { } at ? new ItemMove(id, at.X, at.Y, at.X, at.Y) : (ItemMove?)null).OfType<ItemMove>()];
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private Point? PositionOf(string id) =>
        editor?.Graph.FindNode(id) is { } node ? new Point(node.X, node.Y)
        : editor?.Graph.FindComment(id) is { } comment ? new Point(comment.X, comment.Y)
        : null;

    private bool Exists(string id) => layouts.ContainsKey(id) || editor?.Graph.FindComment(id) is not null;

    private static Rect BoundsOf(GraphComment comment) => new(comment.X, comment.Y, comment.Width, comment.Height);

    private Rect ToScreen(Rect world) => new(world.X * zoom + pan.X, world.Y * zoom + pan.Y, world.Width * zoom, world.Height * zoom);

    private GraphComment? HitCommentHeader(Point world)
    {
        for (int i = editor!.Graph.Comments.Count - 1; i >= 0; i--)
        {
            GraphComment comment = editor.Graph.Comments[i];
            if (BoundsOf(comment).WithHeight(CanvasTheme.CommentHeaderHeight).Contains(world))
            {
                return comment;
            }
        }

        return null;
    }

    private GraphComment? HitCommentGrip(Point world)
    {
        for (int i = editor!.Graph.Comments.Count - 1; i >= 0; i--)
        {
            Rect bounds = BoundsOf(editor.Graph.Comments[i]);
            if (new Rect(bounds.Right - CanvasTheme.ResizeGrip, bounds.Bottom - CanvasTheme.ResizeGrip, CanvasTheme.ResizeGrip, CanvasTheme.ResizeGrip).Contains(world))
            {
                return editor.Graph.Comments[i];
            }
        }

        return null;
    }

    private void DrawComment(DrawingContext context, GraphComment comment)
    {
        Rect bounds = BoundsOf(comment);
        double radius = CanvasTheme.CornerRadius;
        context.DrawRectangle(CanvasTheme.CommentBody, null, bounds, radius, radius);
        Rect header = bounds.WithHeight(Math.Min(CanvasTheme.CommentHeaderHeight, bounds.Height));
        context.DrawRectangle(CanvasTheme.CommentHeader, null, header, radius, radius);

        // The title is the first line of the text, clipped to the box.
        string title = comment.Text.Split('\n', 2)[0].TrimEnd('\r');
        FormattedText text = Text(title, CanvasTheme.CommentFontSize, CanvasTheme.Title, bold: true);
        using (context.PushClip(header.Deflate(new Thickness(10, 0))))
        {
            context.DrawText(text, new Point(bounds.X + 10, bounds.Y + ((CanvasTheme.CommentHeaderHeight - text.Height) / 2)));
        }

        context.DrawRectangle(null, selection.Contains(comment.Id) ? CanvasTheme.NodeSelected : CanvasTheme.CommentBorder, bounds, radius, radius);
        for (double offset = 4; offset <= 12; offset += 4)
        {
            context.DrawLine(CanvasTheme.CommentGrip, new Point(bounds.Right - offset, bounds.Bottom - 3), new Point(bounds.Right - 3, bounds.Bottom - offset));
        }
    }

    private void UpdateConnectTarget()
    {
        connectTarget = null;
        if (editor is null || connectFrom is null || HitPin(connectCurrentWorld) is not { } pin || pin == connectFrom)
        {
            return;
        }

        ConnectionCheck check = editor.CanConnect(connectFrom, pin, out _);
        connectTarget = (pin, check);
        if (!check.IsAllowed)
        {
            StatusChanged?.Invoke(this, check.Error!);
        }
    }

    private void UpdateHover(Point world)
    {
        PinRef? pin = HitPin(world);
        if (pin != hoveredPin)
        {
            hoveredPin = pin;
            InvalidateVisual();
        }

        string? message = pin is not null ? PinMessage(pin)
            : HitReroute(world) is not null ? "Reroute point: drag to move it, Alt+click to remove it."
            : HitNode(world) is null && HitWire(world) is not null ? "Wire: double-click to add a reroute point."
            : null;
        if (message is not null && message != hoverMessage)
        {
            StatusChanged?.Invoke(this, message);
        }

        hoverMessage = message;
    }

    private string? PinMessage(PinRef pin)
    {
        if (editor?.FindPin(pin) is not { } definition)
        {
            return null;
        }

        string type = definition.DataType is null ? "execution" : definition.DataType.DisplayName;
        string direction = definition.Direction == PinDirection.Input ? "input" : "output";
        return $"{(definition.DisplayName.Length == 0 ? "Execution" : definition.DisplayName)}: {type} {direction}. Drag to connect, Alt+click to disconnect.";
    }

    private void DrawGrid(DrawingContext context, Rect visible)
    {
        const double minor = 16;
        const double major = 128;
        if (zoom >= 0.6)
        {
            DrawGridLines(context, visible, minor, CanvasTheme.GridMinor);
        }

        DrawGridLines(context, visible, major, CanvasTheme.GridMajor);
    }

    private static void DrawGridLines(DrawingContext context, Rect visible, double spacing, IPen pen)
    {
        for (double x = Math.Floor(visible.Left / spacing) * spacing; x <= visible.Right; x += spacing)
        {
            context.DrawLine(pen, new Point(x, visible.Top), new Point(x, visible.Bottom));
        }

        for (double y = Math.Floor(visible.Top / spacing) * spacing; y <= visible.Bottom; y += spacing)
        {
            context.DrawLine(pen, new Point(visible.Left, y), new Point(visible.Right, y));
        }
    }

    private void DrawConnections(DrawingContext context, Rect visible)
    {
        foreach (GraphConnection connection in editor!.Graph.Connections)
        {
            // Cheap test first: a wire stays near the nodes it joins and its reroute points.
            if (!layouts.TryGetValue(connection.From.Node, out NodeLayout? fromNode) || !layouts.TryGetValue(connection.To.Node, out NodeLayout? toNode))
            {
                continue;
            }

            Rect reach = fromNode.Bounds.Union(toNode.Bounds);
            foreach (GraphPoint point in connection.Points ?? [])
            {
                reach = reach.Union(new Rect(point.X, point.Y, 0, 0));
            }

            if (!reach.Inflate(120).Intersects(visible) || WirePath(connection) is not { } wire)
            {
                continue;
            }

            drawnWires++;
            IBrush brush = CanvasTheme.ForType(wire.Source.DataType);
            IPen pen = Pen(brush, wire.Source.IsExecution ? 3 : 2);
            for (int i = 0; i + 1 < wire.Points.Length; i++)
            {
                DrawWire(context, wire.Points[i], wire.Points[i + 1], pen);
            }

            for (int i = 1; i + 1 < wire.Points.Length; i++)
            {
                context.DrawEllipse(brush, null, wire.Points[i], CanvasTheme.RerouteRadius, CanvasTheme.RerouteRadius);
            }
        }
    }

    /// <summary>The points a wire passes through: its output pin, its reroute points in order, and its input pin.</summary>
    private (PinDefinition Source, Point[] Points)? WirePath(GraphConnection connection)
    {
        if (FindPinLayout(connection.From) is not { } from || FindPinLayout(connection.To) is not { } to)
        {
            return null;
        }

        return (from.Pin, [from.Center, .. connection.Points?.Select(point => new Point(point.X, point.Y)) ?? [], to.Center]);
    }

    private static Rect BoundsOf(Point[] points)
    {
        double left = points.Min(point => point.X), top = points.Min(point => point.Y);
        return new Rect(left, top, points.Max(point => point.X) - left, points.Max(point => point.Y) - top);
    }

    /// <summary>Wires leave and enter pins horizontally; the control points sit this far out.</summary>
    private static double WireHandle(Point from, Point to) => Math.Max(40, Math.Abs(to.X - from.X) * 0.5);

    private static Point WirePoint(Point from, Point to, double t)
    {
        double handle = WireHandle(from, to);
        double u = 1 - t;
        double a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
        return new Point(
            (a * from.X) + (b * (from.X + handle)) + (c * (to.X - handle)) + (d * to.X),
            (a * from.Y) + (b * from.Y) + (c * to.Y) + (d * to.Y));
    }

    /// <summary>The wire under the pointer and which segment was hit (0 runs from the output pin to the first reroute point).</summary>
    private (GraphConnection Connection, int Segment)? HitWire(Point world)
    {
        double tolerance = CanvasTheme.WireHitDistance / Math.Min(zoom, 1);
        for (int c = editor!.Graph.Connections.Count - 1; c >= 0; c--)
        {
            GraphConnection connection = editor.Graph.Connections[c];
            if (WirePath(connection) is not { } wire)
            {
                continue;
            }

            for (int segment = 0; segment + 1 < wire.Points.Length; segment++)
            {
                (Point from, Point to) = (wire.Points[segment], wire.Points[segment + 1]);
                if (!BoundsOf([from, to]).Inflate(WireHandle(from, to) + tolerance).Contains(world))
                {
                    continue;
                }

                // Sampling is accurate enough for picking; the step stays under the tolerance for typical wire lengths.
                const int Samples = 32;
                Point previous = from;
                for (int i = 1; i <= Samples; i++)
                {
                    Point next = WirePoint(from, to, (double)i / Samples);
                    if (DistanceToSegment(world, previous, next) <= tolerance)
                    {
                        return (connection, segment);
                    }

                    previous = next;
                }
            }
        }

        return null;
    }

    private static double DistanceToSegment(Point point, Point a, Point b)
    {
        Vector ab = b - a;
        double lengthSquared = (ab.X * ab.X) + (ab.Y * ab.Y);
        double t = lengthSquared == 0 ? 0 : Math.Clamp((((point.X - a.X) * ab.X) + ((point.Y - a.Y) * ab.Y)) / lengthSquared, 0, 1);
        return Point.Distance(point, new Point(a.X + (t * ab.X), a.Y + (t * ab.Y)));
    }

    private (GraphConnection Connection, int Index)? HitReroute(Point world)
    {
        double hitRadius = CanvasTheme.PinHitRadius / Math.Min(zoom, 1);
        for (int c = editor!.Graph.Connections.Count - 1; c >= 0; c--)
        {
            GraphConnection connection = editor.Graph.Connections[c];
            for (int i = 0; i < (connection.Points?.Count ?? 0); i++)
            {
                if (Point.Distance(new Point(connection.Points![i].X, connection.Points[i].Y), world) <= hitRadius)
                {
                    return (connection, i);
                }
            }
        }

        return null;
    }

    private static void DrawWire(DrawingContext context, Point from, Point to, IPen pen)
    {
        double handle = WireHandle(from, to);
        var geometry = new StreamGeometry();
        using (StreamGeometryContext path = geometry.Open())
        {
            path.BeginFigure(from, isFilled: false);
            path.CubicBezierTo(new Point(from.X + handle, from.Y), new Point(to.X - handle, to.Y), to);
            path.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, pen, geometry);
    }

    private void DrawNode(DrawingContext context, NodeLayout layout)
    {
        Rect bounds = layout.Bounds;
        double radius = CanvasTheme.CornerRadius;
        context.DrawRectangle(CanvasTheme.NodeBody, null, bounds, radius, radius);

        IBrush accent = CanvasTheme.ForCategory(layout.Definition.Category);
        var header = new Rect(bounds.X, bounds.Y, bounds.Width, CanvasTheme.HeaderHeight);
        context.DrawRectangle(accent, null, header, radius, radius);
        context.FillRectangle(accent, new Rect(header.X, header.Bottom - radius, header.Width, radius));

        FormattedText title = Text(layout.Definition.DisplayName, CanvasTheme.FontSize, CanvasTheme.Title, bold: true);
        context.DrawText(title, new Point(bounds.X + 12, bounds.Y + ((CanvasTheme.HeaderHeight - title.Height) / 2)));

        if (layout.Preview is { } preview)
        {
            FormattedText value = Text(preview, CanvasTheme.SmallFontSize, CanvasTheme.ValuePreview);
            context.DrawText(value, new Point(bounds.X + 12, header.Bottom + ((CanvasTheme.PreviewHeight - value.Height) / 2)));
        }

        foreach (PinLayout pin in layout.Pins)
        {
            DrawPin(context, layout, pin);
        }

        IPen border = selection.Contains(layout.Node.Id)
            ? CanvasTheme.NodeSelected
            : nodeStates.GetValueOrDefault(layout.Node.Id, DiagnosticSeverity.Info) switch
            {
                DiagnosticSeverity.Error => CanvasTheme.NodeError,
                DiagnosticSeverity.Warning => CanvasTheme.NodeWarning,
                _ => CanvasTheme.NodeBorder,
            };
        context.DrawRectangle(null, border, bounds, radius, radius);

        if (selection.Contains(layout.Node.Id) && nodeStates.GetValueOrDefault(layout.Node.Id) == DiagnosticSeverity.Error)
        {
            // Keep the error visible on selected nodes with an inner outline.
            context.DrawRectangle(null, CanvasTheme.NodeError, bounds.Deflate(3), radius - 2, radius - 2);
        }
    }

    private void DrawPin(DrawingContext context, NodeLayout layout, PinLayout pin)
    {
        var reference = new PinRef(layout.Node.Id, pin.Pin.Id);
        bool connected = IsConnected(reference);
        bool highlighted = reference == hoveredPin || connectTarget?.Pin == reference;
        IBrush color = connectTarget is { } target && target.Pin == reference
            ? (target.Check.IsAllowed ? CanvasTheme.Valid : CanvasTheme.Invalid)
            : CanvasTheme.ForType(pin.Pin.DataType);
        double radius = CanvasTheme.PinRadius + (highlighted ? 1.5 : 0);
        Point center = pin.Center;
        bool input = pin.Pin.Direction == PinDirection.Input;
        Point pinCenter = center;

        if (pin.Pin.IsExecution)
        {
            // Execution pins are arrows; data pins are circles.
            var arrow = new StreamGeometry();
            using (StreamGeometryContext path = arrow.Open())
            {
                path.BeginFigure(new Point(pinCenter.X - radius, pinCenter.Y - radius), isFilled: true);
                path.LineTo(new Point(pinCenter.X + (radius * 0.2), pinCenter.Y - radius));
                path.LineTo(new Point(pinCenter.X + radius + 2, pinCenter.Y));
                path.LineTo(new Point(pinCenter.X + (radius * 0.2), pinCenter.Y + radius));
                path.LineTo(new Point(pinCenter.X - radius, pinCenter.Y + radius));
                path.EndFigure(isClosed: true);
            }

            context.DrawGeometry(connected ? color : null, Pen(color, 1.5), arrow);
        }
        else
        {
            context.DrawEllipse(connected ? color : null, Pen(color, 1.5), pinCenter, radius, radius);
        }

        string label = NodeLayout.PinLabel(pin.Pin);
        if (label.Length == 0)
        {
            return;
        }

        FormattedText name = Text(pin.Pin.DisplayName, CanvasTheme.SmallFontSize, CanvasTheme.Label);
        FormattedText? type = pin.Pin.DataType is null ? null : Text(pin.Pin.DataType.DisplayName, CanvasTheme.SmallFontSize, CanvasTheme.TypeLabel);
        double y = center.Y - (name.Height / 2);
        if (input)
        {
            double x = pinCenter.X + radius + 8;
            context.DrawText(name, new Point(x, y));
            if (type is not null)
            {
                context.DrawText(type, new Point(x + name.Width + 6, y));
            }
        }
        else
        {
            double x = pinCenter.X - radius - 8;
            if (type is not null)
            {
                x -= type.Width;
                context.DrawText(type, new Point(x, y));
                x -= 6;
            }

            context.DrawText(name, new Point(x - name.Width, y));
        }
    }

    private bool IsConnected(PinRef pin) => connectedPins.Contains(pin);

    private PinLayout? FindPinLayout(PinRef pin)
    {
        if (!layouts.TryGetValue(pin.Node, out NodeLayout? layout))
        {
            return null;
        }

        foreach (PinLayout candidate in layout.Pins)
        {
            if (candidate.Pin.Id == pin.Pin)
            {
                return candidate;
            }
        }

        return null;
    }

    private PinRef? HitPin(Point world)
    {
        double hitRadius = CanvasTheme.PinHitRadius / Math.Min(zoom, 1);
        for (int i = editor!.Graph.Nodes.Count - 1; i >= 0; i--)
        {
            if (!layouts.TryGetValue(editor.Graph.Nodes[i].Id, out NodeLayout? layout) || !layout.Bounds.Inflate(hitRadius).Contains(world))
            {
                continue;
            }

            foreach (PinLayout pin in layout.Pins)
            {
                if (Point.Distance(pin.Center, world) <= hitRadius)
                {
                    return new PinRef(layout.Node.Id, pin.Pin.Id);
                }
            }
        }

        return null;
    }

    private NodeLayout? HitNode(Point world)
    {
        for (int i = editor!.Graph.Nodes.Count - 1; i >= 0; i--)
        {
            if (layouts.TryGetValue(editor.Graph.Nodes[i].Id, out NodeLayout? layout) && layout.Bounds.Contains(world))
            {
                return layout;
            }
        }

        return null;
    }

    private Rect MarqueeWorld() => new Rect(ToWorld(pressScreen), ToWorld(lastScreen)).Normalize();


    private double MeasureText(string text, double size) => Text(text, size, CanvasTheme.Label).Width;

    private FormattedText Text(string text, double size, IBrush brush, bool bold = false)
    {
        if (!textCache.TryGetValue((text, size, brush, bold), out FormattedText? formatted))
        {
            formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                bold ? CanvasTheme.TitleTypeface : CanvasTheme.Typeface, size, brush);
            textCache[(text, size, brush, bold)] = formatted;
        }

        return formatted;
    }

    private IPen Pen(IBrush brush, double thickness)
    {
        if (!pens.TryGetValue((brush, thickness), out IPen? pen))
        {
            pen = new ImmutablePen((IImmutableBrush)brush, thickness);
            pens[(brush, thickness)] = pen;
        }

        return pen;
    }
}

/// <summary>Maps between graph coordinates and the minimap drawn at <see cref="Frame"/> in control coordinates.</summary>
internal sealed record MinimapMapping(Rect Frame, double Scale, Point Origin)
{
    public Rect ToScreen(Rect world) => new(Origin.X + (world.X * Scale), Origin.Y + (world.Y * Scale), world.Width * Scale, world.Height * Scale);

    public Point ToWorld(Point screen) => new((screen.X - Origin.X) / Scale, (screen.Y - Origin.Y) / Scale);
}

/// <summary>A request to rename a comment; <see cref="Screen"/> is its title bar in control coordinates.</summary>
internal sealed class CommentEditRequestedEventArgs(string commentId, Rect screen) : EventArgs
{
    public string CommentId { get; } = commentId;

    public Rect Screen { get; } = screen;
}

/// <summary>A request to edit <see cref="Property"/> of a node; <see cref="Screen"/> is its value row in control coordinates.</summary>
internal sealed class ValueEditRequestedEventArgs(string nodeId, PropertyDefinition property, Rect screen) : EventArgs
{
    public string NodeId { get; } = nodeId;

    public PropertyDefinition Property { get; } = property;

    public Rect Screen { get; } = screen;
}

/// <summary>Where the action menu was requested, in graph and control coordinates.</summary>
internal sealed class ActionMenuRequestedEventArgs(Point world, Point screen) : EventArgs
{
    public Point World { get; } = world;

    public Point Screen { get; } = screen;
}

/// <summary>Where a wire from <see cref="Source"/> was released, in graph and control coordinates.</summary>
internal sealed class WireDroppedEventArgs(PinRef source, Point world, Point screen) : EventArgs
{
    public PinRef Source { get; } = source;

    public Point World { get; } = world;

    public Point Screen { get; } = screen;
}
