using VisualStudioLikeIDE.Compiler.Lowering;
using VisualStudioLikeIDE.Compiler.Nodes;
using VisualStudioLikeIDE.GraphModel;

namespace VisualStudioLikeIDE.NodeEditor;

/// <summary>A position change of a node or comment, recorded once when a drag ends.</summary>
public readonly record struct ItemMove(string Id, double FromX, double FromY, double ToX, double ToY);

/// <summary>What <see cref="GraphEditor.Paste"/> inserted.</summary>
public sealed record PasteResult(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphComment> Comments)
{
    public IEnumerable<string> Ids => Nodes.Select(node => node.Id).Concat(Comments.Select(comment => comment.Id));
}

/// <summary>
/// Every change to a graph made in the editor goes through this class, so each change is validated,
/// undoable, and announced through <see cref="Changed"/>.
/// </summary>
/// <remarks>
/// Transient interaction state (hover, drag previews, selection) belongs to the view, not here.
/// While a node is dragged the view updates positions directly for responsiveness and calls
/// <see cref="CommitMove"/> once at the end, which records a single undo step.
/// </remarks>
public sealed partial class GraphEditor
{
    /// <summary>The graph ID that marks a serialized <see cref="CopyFragment"/> on the clipboard.</summary>
    public const string FragmentId = "vside-clipboard-fragment";

    private readonly Stack<Edit> undo = new();
    private readonly Stack<Edit> redo = new();
    private int savedUndoDepth;

    public GraphEditor(GraphDocument graph, NodeLibrary library)
    {
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
        Library = library ?? throw new ArgumentNullException(nameof(library));
    }

    /// <summary>Raised after every edit, undo, and redo.</summary>
    public event EventHandler? Changed;

    public GraphDocument Graph { get; }

    public NodeLibrary Library { get; }

    /// <summary>
    /// The project's function graphs, which call nodes refer to. The owner keeps this current when functions are added;
    /// renaming a function or changing its signature needs no update, because call nodes resolve against the live documents.
    /// </summary>
    public IReadOnlyList<GraphDocument> Functions { get; set; } = [];

    public bool CanUndo => undo.Count > 0;

    public bool CanRedo => redo.Count > 0;

    public string? UndoDescription => undo.TryPeek(out Edit? edit) ? edit.Description : null;

    public string? RedoDescription => redo.TryPeek(out Edit? edit) ? edit.Description : null;

    /// <summary>True when the graph differs from the last saved state.</summary>
    public bool IsDirty => undo.Count != savedUndoDepth;

    public void MarkSaved() => savedUndoDepth = undo.Count;

    public NodeDefinition? DefinitionOf(GraphNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return Library.Resolve(node, Graph, Functions);
    }

    /// <summary>Every node that can be added to this graph, including variable nodes and a call node per function.</summary>
    public IEnumerable<NodeDefinition> AvailableDefinitions => Library.DefinitionsFor(Graph, Functions);

    public GraphNode AddNode(string typeId, double x, double y) => AddNode(FindDefinition(typeId), x, y);

    public GraphNode AddNode(NodeDefinition definition, double x, double y)
    {
        (GraphNode node, Edit edit) = CreateAddEdit(definition, x, y);
        Execute(edit);
        return node;
    }

    /// <summary>
    /// Returns the first pin of <paramref name="definition"/> that a new node could connect to <paramref name="source"/>,
    /// or null when the node type has no compatible pin. Used to offer only fitting nodes when a wire is dropped on empty space.
    /// </summary>
    public PinDefinition? FindCompatiblePin(NodeDefinition definition, PinRef source)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (FindPin(source) is not { } sourcePin)
        {
            return null;
        }

        return definition.Pins.FirstOrDefault(pin => sourcePin.Direction == PinDirection.Output
            ? ConnectionRules.Check(sourcePin, pin).IsAllowed
            : ConnectionRules.Check(pin, sourcePin).IsAllowed);
    }

    /// <summary>
    /// Adds a node and connects its first compatible pin to <paramref name="source"/>, as one undo step.
    /// </summary>
    public GraphNode AddConnectedNode(string typeId, double x, double y, PinRef source) => AddConnectedNode(FindDefinition(typeId), x, y, source);

    public GraphNode AddConnectedNode(NodeDefinition definition, double x, double y, PinRef source)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(source);
        PinDefinition pin = FindCompatiblePin(definition, source)
            ?? throw new ArgumentException($"'{definition.DisplayName}' has no pin that can connect to this pin.", nameof(definition));

        (GraphNode node, Edit add) = CreateAddEdit(definition, x, y);
        GraphConnection connection = pin.Direction == PinDirection.Input
            ? new(source, new PinRef(node.Id, pin.Id))
            : new(new PinRef(node.Id, pin.Id), source);
        Edit connect = CreateConnectEdit(connection, FindPin(connection.From) ?? pin, FindPin(connection.To) ?? pin);
        Execute(new Edit(add.Description, () => { add.Apply(); connect.Apply(); }, () => { connect.Revert(); add.Revert(); }));
        return node;
    }

    /// <summary>
    /// Copies nodes, comments, and the connections between the nodes into a standalone graph, the clipboard format.
    /// Connections to nodes outside the set are left out.
    /// </summary>
    public GraphDocument CopyFragment(IReadOnlyCollection<string> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var ids = new HashSet<string>(itemIds, StringComparer.Ordinal);
        var fragment = new GraphDocument { Id = FragmentId, Name = "Fragment", Kind = Graph.Kind };
        fragment.Nodes.AddRange(Graph.Nodes.Where(node => ids.Contains(node.Id)).Select(node => new GraphNode
        {
            Id = node.Id,
            Type = node.Type,
            X = node.X,
            Y = node.Y,
            Properties = new(node.Properties, StringComparer.Ordinal),
        }));
        fragment.Connections.AddRange(Graph.Connections
            .Where(connection => ids.Contains(connection.From.Node) && ids.Contains(connection.To.Node))
            .Select(connection => connection with { Points = connection.Points is null ? null : [.. connection.Points] }));
        fragment.Comments.AddRange(Graph.Comments.Where(comment => ids.Contains(comment.Id)).Select(CopyOf));
        return fragment;
    }

    /// <summary>
    /// Inserts copies of a fragment's nodes and comments with new IDs, its top-left corner at (<paramref name="x"/>, <paramref name="y"/>),
    /// as one undo step. The fragment may come from the system clipboard, so connections that do not join two of its nodes
    /// are dropped; anything else invalid is left for the compiler to report.
    /// </summary>
    public PasteResult Paste(GraphDocument fragment, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        if (fragment.Nodes.Count == 0 && fragment.Comments.Count == 0)
        {
            return new([], []);
        }

        double left = fragment.Nodes.Select(node => node.X).Concat(fragment.Comments.Select(comment => comment.X)).Min();
        double top = fragment.Nodes.Select(node => node.Y).Concat(fragment.Comments.Select(comment => comment.Y)).Min();
        var newIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var nodes = new List<GraphNode>();
        foreach (GraphNode node in fragment.Nodes)
        {
            if (newIds.ContainsKey(node.Id))
            {
                continue;
            }

            var copy = new GraphNode
            {
                Id = GraphDocument.NewId(),
                Type = node.Type,
                X = Math.Round(x + node.X - left),
                Y = Math.Round(y + node.Y - top),
                Properties = new(node.Properties, StringComparer.Ordinal),
            };
            newIds[node.Id] = copy.Id;
            nodes.Add(copy);
        }

        List<GraphConnection> connections = [.. fragment.Connections
            .Where(connection => newIds.ContainsKey(connection.From.Node) && newIds.ContainsKey(connection.To.Node))
            .Select(connection => new GraphConnection(
                new PinRef(newIds[connection.From.Node], connection.From.Pin),
                new PinRef(newIds[connection.To.Node], connection.To.Pin))
            {
                Points = connection.Points?.Select(point => new GraphPoint(Math.Round(x + point.X - left), Math.Round(y + point.Y - top))).ToList(),
            })
            .Distinct()];

        List<GraphComment> comments = [.. fragment.Comments.Select(comment =>
        {
            GraphComment copy = CopyOf(comment, GraphDocument.NewId());
            copy.X = Math.Round(x + comment.X - left);
            copy.Y = Math.Round(y + comment.Y - top);
            return copy;
        })];

        int count = nodes.Count + comments.Count;
        Execute(new Edit(
            count == 1 ? "Paste item" : $"Paste {count} items",
            () =>
            {
                Graph.Comments.AddRange(comments);
                Graph.Nodes.AddRange(nodes);
                Graph.Connections.AddRange(connections);
            },
            () =>
            {
                Graph.Connections.RemoveAll(connections.Contains);
                Graph.Nodes.RemoveAll(nodes.Contains);
                Graph.Comments.RemoveAll(comments.Contains);
            }));
        return new(nodes, comments);
    }

    /// <summary>Inserts a reroute point into a wire; index 0 is nearest the output pin.</summary>
    public void AddReroute(GraphConnection connection, int index, double x, double y)
    {
        GraphConnection target = FindConnection(connection);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, target.Points?.Count ?? 0);
        var point = new GraphPoint(x, y);
        Execute(new Edit("Add reroute point", () => (target.Points ??= []).Insert(index, point), () => RemovePoint(target, index)));
    }

    public void RemoveReroute(GraphConnection connection, int index)
    {
        GraphConnection target = FindConnection(connection);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, target.Points?.Count ?? 0);
        GraphPoint point = target.Points![index];
        Execute(new Edit("Remove reroute point", () => RemovePoint(target, index), () => (target.Points ??= []).Insert(index, point)));
    }

    /// <summary>Records a reroute point move that the view has already applied.</summary>
    public void CommitRerouteMove(GraphConnection connection, int index, double fromX, double fromY)
    {
        GraphConnection target = FindConnection(connection);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, target.Points?.Count ?? 0);
        GraphPoint to = target.Points![index];
        var from = new GraphPoint(fromX, fromY);
        if (to == from)
        {
            return;
        }

        Execute(new Edit("Move reroute point", () => target.Points![index] = to, () => target.Points![index] = from), alreadyApplied: true);
    }

    /// <summary>Adds a comment box. Comments are drawn behind nodes and never affect generated code.</summary>
    public GraphComment AddComment(double x, double y, double width, double height)
    {
        var comment = new GraphComment { Id = GraphDocument.NewId(), X = x, Y = y, Width = width, Height = height, Text = "Comment" };
        Execute(new Edit("Add comment", () => Graph.Comments.Add(comment), () => Graph.Comments.Remove(comment)));
        return comment;
    }

    public void SetCommentText(string commentId, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        GraphComment comment = Graph.FindComment(commentId) ?? throw new ArgumentException($"Unknown comment '{commentId}'.", nameof(commentId));
        string previous = comment.Text;
        if (previous == text)
        {
            return;
        }

        Execute(new Edit("Edit comment", () => comment.Text = text, () => comment.Text = previous));
    }

    /// <summary>Records a comment resize that the view has already applied.</summary>
    public void CommitResize(string commentId, double fromWidth, double fromHeight)
    {
        GraphComment comment = Graph.FindComment(commentId) ?? throw new ArgumentException($"Unknown comment '{commentId}'.", nameof(commentId));
        (double toWidth, double toHeight) = (comment.Width, comment.Height);
        if (toWidth == fromWidth && toHeight == fromHeight)
        {
            return;
        }

        Execute(new Edit(
            "Resize comment",
            () => (comment.Width, comment.Height) = (toWidth, toHeight),
            () => (comment.Width, comment.Height) = (fromWidth, fromHeight)),
            alreadyApplied: true);
    }

    /// <summary>Removes nodes (with every connection attached to them) and comments.</summary>
    public void Delete(IReadOnlyCollection<string> itemIds)
    {
        if (CreateDeleteEdit(itemIds) is { } edit)
        {
            Execute(edit);
        }
    }

    private Edit? CreateDeleteEdit(IReadOnlyCollection<string> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var ids = new HashSet<string>(itemIds, StringComparer.Ordinal);
        List<(int Index, GraphNode Node)> nodes = [.. Graph.Nodes.Select((node, index) => (index, node)).Where(entry => ids.Contains(entry.node.Id))];
        List<(int Index, GraphComment Comment)> comments = [.. Graph.Comments.Select((comment, index) => (index, comment)).Where(entry => ids.Contains(entry.comment.Id))];
        if (nodes.Count == 0 && comments.Count == 0)
        {
            return null;
        }

        List<(int Index, GraphConnection Connection)> connections = [.. Graph.Connections
            .Select((connection, index) => (index, connection))
            .Where(entry => ids.Contains(entry.connection.From.Node) || ids.Contains(entry.connection.To.Node))];

        int count = nodes.Count + comments.Count;
        return new Edit(
            count == 1 ? "Delete item" : $"Delete {count} items",
            () =>
            {
                Graph.Connections.RemoveAll(connection => ids.Contains(connection.From.Node) || ids.Contains(connection.To.Node));
                Graph.Nodes.RemoveAll(node => ids.Contains(node.Id));
                Graph.Comments.RemoveAll(comment => ids.Contains(comment.Id));
            },
            () =>
            {
                // Restore original order so serialization and undo are exact.
                foreach ((int index, GraphComment comment) in comments)
                {
                    Graph.Comments.Insert(index, comment);
                }

                foreach ((int index, GraphNode node) in nodes)
                {
                    Graph.Nodes.Insert(index, node);
                }

                foreach ((int index, GraphConnection connection) in connections)
                {
                    Graph.Connections.Insert(index, connection);
                }
            });
    }

    /// <summary>Records node and comment moves that the view has already applied.</summary>
    public void CommitMove(IReadOnlyList<ItemMove> moves) => Move(moves, alreadyApplied: true);

    /// <summary>Moves nodes and comments as one undoable step, for example the result of <see cref="LayoutTools"/>.</summary>
    public void MoveItems(IReadOnlyList<ItemMove> moves) => Move(moves, alreadyApplied: false);

    private void Move(IReadOnlyList<ItemMove> moves, bool alreadyApplied)
    {
        ArgumentNullException.ThrowIfNull(moves);
        ItemMove[] actual = [.. moves.Where(move => move.FromX != move.ToX || move.FromY != move.ToY)];
        if (actual.Length == 0)
        {
            return;
        }

        void Place(bool forward)
        {
            foreach (ItemMove move in actual)
            {
                (double x, double y) = forward ? (move.ToX, move.ToY) : (move.FromX, move.FromY);
                if (Graph.FindNode(move.Id) is { } node)
                {
                    (node.X, node.Y) = (x, y);
                }
                else if (Graph.FindComment(move.Id) is { } comment)
                {
                    (comment.X, comment.Y) = (x, y);
                }
            }
        }

        Execute(new Edit(actual.Length == 1 ? "Move item" : $"Move {actual.Length} items", () => Place(true), () => Place(false)), alreadyApplied);
    }

    /// <summary>
    /// Checks whether two pins can be connected. The pins may be given in either order; on success
    /// <paramref name="connection"/> runs from the output to the input.
    /// </summary>
    public ConnectionCheck CanConnect(PinRef first, PinRef second, out GraphConnection? connection)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        connection = null;
        PinDefinition? a = FindPin(first);
        PinDefinition? b = FindPin(second);
        if (a is null || b is null)
        {
            return new(null, "Unknown pin.");
        }

        if (first.Node == second.Node)
        {
            return new(null, "A node cannot connect to itself.");
        }

        (PinRef fromRef, PinDefinition from, PinRef toRef, PinDefinition to) = a.Direction == PinDirection.Output
            ? (first, a, second, b)
            : (second, b, first, a);

        ConnectionCheck check = ConnectionRules.Check(from, to);
        if (check.IsAllowed)
        {
            connection = new GraphConnection(fromRef, toRef);
        }

        return check;
    }

    /// <summary>
    /// Connects two pins. Single-connection pins (data inputs, execution outputs) drop their previous connection,
    /// all as one undo step.
    /// </summary>
    public ConnectionCheck Connect(PinRef first, PinRef second)
    {
        ConnectionCheck check = CanConnect(first, second, out GraphConnection? connection);
        if (!check.IsAllowed || Graph.Connections.Contains(connection!))
        {
            return check;
        }

        Execute(CreateConnectEdit(connection!, FindPin(connection!.From)!, FindPin(connection.To)!));
        return check;
    }

    /// <summary>
    /// Every new connection the rules allow between two nodes, in both directions: outputs of the first node to inputs of
    /// the second come first, each in pin order. Connections that already exist are left out.
    /// </summary>
    public IReadOnlyList<GraphConnection> PossibleConnections(string firstNodeId, string secondNodeId)
    {
        GraphNode first = Graph.FindNode(firstNodeId) ?? throw new ArgumentException("Unknown node.", nameof(firstNodeId));
        GraphNode second = Graph.FindNode(secondNodeId) ?? throw new ArgumentException("Unknown node.", nameof(secondNodeId));
        var result = new List<GraphConnection>();
        foreach ((GraphNode from, GraphNode to) in new[] { (first, second), (second, first) })
        {
            if (DefinitionOf(from) is not { } fromDefinition || DefinitionOf(to) is not { } toDefinition)
            {
                continue;
            }

            foreach (PinDefinition output in fromDefinition.Pins.Where(pin => pin.Direction == PinDirection.Output))
            {
                foreach (PinDefinition input in toDefinition.Pins.Where(pin => pin.Direction == PinDirection.Input))
                {
                    if (CanConnect(new PinRef(from.Id, output.Id), new PinRef(to.Id, input.Id), out GraphConnection? connection).IsAllowed
                        && !Graph.Connections.Contains(connection!))
                    {
                        result.Add(connection!);
                    }
                }
            }
        }

        return result;
    }

    /// <summary>Removes one connection.</summary>
    public void Disconnect(GraphConnection connection)
    {
        GraphConnection target = FindConnection(connection);
        int index = Graph.Connections.IndexOf(target);
        Execute(new Edit("Disconnect pins", () => Graph.Connections.RemoveAt(index), () => Graph.Connections.Insert(index, target)));
    }

    /// <summary>Removes every connection attached to a pin.</summary>
    public void DisconnectPin(PinRef pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        List<(int Index, GraphConnection Connection)> removed = [.. Graph.Connections
            .Select((connection, index) => (index, connection))
            .Where(entry => entry.connection.From == pin || entry.connection.To == pin)];
        if (removed.Count == 0)
        {
            return;
        }

        Execute(new Edit(
            "Disconnect pin",
            () => Graph.Connections.RemoveAll(connection => connection.From == pin || connection.To == pin),
            () =>
            {
                foreach ((int index, GraphConnection connection) in removed)
                {
                    Graph.Connections.Insert(index, connection);
                }
            }));
    }

    /// <summary>Sets a node property. Values are stored even when invalid; the compiler reports them so the user can fix them in place.</summary>
    public void SetProperty(string nodeId, string propertyId, string value)
    {
        GraphNode node = Graph.FindNode(nodeId) ?? throw new ArgumentException($"Unknown node '{nodeId}'.", nameof(nodeId));
        bool hadValue = node.Properties.TryGetValue(propertyId, out string? previous);
        if (hadValue && previous == value)
        {
            return;
        }

        Execute(new Edit(
            "Edit value",
            () => node.Properties[propertyId] = value,
            () =>
            {
                if (hadValue)
                {
                    node.Properties[propertyId] = previous!;
                }
                else
                {
                    node.Properties.Remove(propertyId);
                }
            }));
    }

    public void Undo()
    {
        if (undo.TryPop(out Edit? edit))
        {
            edit.Revert();
            redo.Push(edit);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Redo()
    {
        if (redo.TryPop(out Edit? edit))
        {
            edit.Apply();
            undo.Push(edit);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public PinDefinition? FindPin(PinRef pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        GraphNode? node = Graph.FindNode(pin.Node);
        return node is null ? null : DefinitionOf(node)?.FindPin(pin.Pin);
    }

    // A graph-dependent type is found by id only when this graph has exactly one such definition, like a function's entry node.
    private NodeDefinition FindDefinition(string typeId) =>
        Library.Find(typeId)
        ?? (AvailableDefinitions.Where(definition => definition.TypeId == typeId).ToList() is [NodeDefinition only] ? only : null)
        ?? throw new ArgumentException($"Unknown node type '{typeId}'.", nameof(typeId));

    private (GraphNode Node, Edit Edit) CreateAddEdit(NodeDefinition definition, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var node = new GraphNode { Id = GraphDocument.NewId(), Type = definition.TypeId, X = x, Y = y };
        foreach (PropertyDefinition property in definition.Properties)
        {
            node.Properties[property.Id] = property.DefaultValue;
        }

        foreach ((string key, string value) in definition.InitialProperties)
        {
            node.Properties[key] = value;
        }

        return (node, new Edit($"Add {definition.DisplayName}", () => Graph.Nodes.Add(node), () => Graph.Nodes.Remove(node)));
    }

    /// <summary>Builds the edit that adds <paramref name="connection"/> and drops connections it replaces on single-connection pins.</summary>
    private Edit CreateConnectEdit(GraphConnection connection, PinDefinition from, PinDefinition to)
    {
        List<(int Index, GraphConnection Connection)> replaced = [.. Graph.Connections
            .Select((existing, index) => (index, existing))
            .Where(entry => (ConnectionRules.IsSingleConnection(to) && entry.existing.To == connection.To)
                || (ConnectionRules.IsSingleConnection(from) && entry.existing.From == connection.From))];

        return new Edit(
            "Connect pins",
            () =>
            {
                foreach ((_, GraphConnection old) in replaced)
                {
                    Graph.Connections.Remove(old);
                }

                Graph.Connections.Add(connection);
            },
            () =>
            {
                Graph.Connections.Remove(connection);
                foreach ((int index, GraphConnection old) in replaced)
                {
                    Graph.Connections.Insert(index, old);
                }
            });
    }

    private GraphConnection FindConnection(GraphConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return Graph.Connections.Find(existing => existing == connection)
            ?? throw new ArgumentException("The connection is not part of the graph.", nameof(connection));
    }

    private static void RemovePoint(GraphConnection connection, int index)
    {
        connection.Points!.RemoveAt(index);
        if (connection.Points.Count == 0)
        {
            connection.Points = null;
        }
    }

    private static GraphComment CopyOf(GraphComment comment) => CopyOf(comment, comment.Id);

    private static GraphComment CopyOf(GraphComment comment, string id) =>
        new() { Id = id, X = comment.X, Y = comment.Y, Width = comment.Width, Height = comment.Height, Text = comment.Text };

    /// <summary>Applies and records an edit. <paramref name="alreadyApplied"/> is for changes the view made live, such as a drag.</summary>
    private void Execute(Edit edit, bool alreadyApplied = false)
    {
        if (!alreadyApplied)
        {
            edit.Apply();
        }

        undo.Push(edit);
        redo.Clear();
        if (savedUndoDepth > undo.Count)
        {
            // The saved state was on the redo stack, which is now gone.
            savedUndoDepth = -1;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed record Edit(string Description, Action Apply, Action Revert);
}
