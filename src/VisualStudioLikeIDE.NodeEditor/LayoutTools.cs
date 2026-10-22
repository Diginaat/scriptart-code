namespace VisualStudioLikeIDE.NodeEditor;

/// <summary>Which edge or center <see cref="LayoutTools.Align"/> lines items up on.</summary>
public enum Alignment
{
    Left,
    Center,
    Right,
    Top,
    Middle,
    Bottom,
}

public enum Axis
{
    Horizontal,
    Vertical,
}

/// <summary>The position and size of an item on the canvas. Sizes come from the view, which owns node layout.</summary>
public readonly record struct ItemBounds(string Id, double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;
}

/// <summary>
/// Alignment and distribution of selected items. The results are moves for <see cref="GraphEditor.MoveItems"/>;
/// positions are rounded to whole canvas units like dragged positions, and items that would not move are left out.
/// </summary>
public static class LayoutTools
{
    public static IReadOnlyList<ItemMove> Align(IReadOnlyList<ItemBounds> items, Alignment alignment)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count < 2)
        {
            return [];
        }

        double left = items.Min(item => item.X);
        double right = items.Max(item => item.Right);
        double top = items.Min(item => item.Y);
        double bottom = items.Max(item => item.Bottom);
        return Changed(items.Select(item => alignment switch
        {
            Alignment.Left => (item, left, item.Y),
            Alignment.Center => (item, ((left + right) / 2) - (item.Width / 2), item.Y),
            Alignment.Right => (item, right - item.Width, item.Y),
            Alignment.Top => (item, item.X, top),
            Alignment.Middle => (item, item.X, ((top + bottom) / 2) - (item.Height / 2)),
            Alignment.Bottom => (item, item.X, bottom - item.Height),
            _ => throw new ArgumentOutOfRangeException(nameof(alignment)),
        }));
    }

    /// <summary>Spaces items so the gaps between neighbors are equal. The first and last item along the axis stay in place.</summary>
    public static IReadOnlyList<ItemMove> Distribute(IReadOnlyList<ItemBounds> items, Axis axis)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count < 3)
        {
            return [];
        }

        bool horizontal = axis == Axis.Horizontal;
        ItemBounds[] ordered = [.. items
            .OrderBy(item => horizontal ? item.X : item.Y)
            .ThenBy(item => item.Id, StringComparer.Ordinal)];
        double start = horizontal ? ordered[0].X : ordered[0].Y;
        double end = horizontal ? ordered.Max(item => item.Right) : ordered.Max(item => item.Bottom);
        double gap = (end - start - ordered.Sum(item => horizontal ? item.Width : item.Height)) / (ordered.Length - 1);

        var targets = new List<(ItemBounds Item, double X, double Y)>();
        double position = start;
        foreach (ItemBounds item in ordered)
        {
            targets.Add(horizontal ? (item, position, item.Y) : (item, item.X, position));
            position += (horizontal ? item.Width : item.Height) + gap;
        }

        return Changed(targets);
    }

    private static ItemMove[] Changed(IEnumerable<(ItemBounds Item, double X, double Y)> targets) =>
        [.. targets
            .Select(target => new ItemMove(target.Item.Id, target.Item.X, target.Item.Y, Math.Round(target.X), Math.Round(target.Y)))
            .Where(move => move.FromX != move.ToX || move.FromY != move.ToY)];
}
