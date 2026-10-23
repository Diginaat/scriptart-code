namespace ScriptArt.NodeEditor;

/// <summary>An axis-aligned rectangle in canvas units.</summary>
public readonly record struct Area(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public bool Intersects(Area other) => X <= other.Right && other.X <= Right && Y <= other.Bottom && other.Y <= Bottom;
}

/// <summary>
/// Finds the items whose bounds intersect a rectangle without testing every item: a uniform grid maps each cell to the items that
/// overlap it (D-040). The canvas uses it to draw only what is visible and to hit-test under the pointer, so both cost time in
/// proportion to the items nearby rather than to the size of the graph.
/// </summary>
/// <typeparam name="T">The item type, compared with its default equality.</typeparam>
public sealed class SpatialIndex<T>
    where T : notnull
{
    private readonly double cellSize;
    private readonly Dictionary<(int X, int Y), List<T>> cells = [];
    private readonly Dictionary<T, Area> items = [];

    /// <param name="cellSize">Width and height of a grid cell; about the size of a typical item works well.</param>
    public SpatialIndex(double cellSize = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellSize);
        this.cellSize = cellSize;
    }

    public int Count => items.Count;

    /// <summary>Adds <paramref name="item"/>, or moves it when it is already in the index.</summary>
    public void Set(T item, Area bounds)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (items.TryGetValue(item, out Area old))
        {
            if (old == bounds)
            {
                return;
            }

            RemoveFromCells(item, old);
        }

        items[item] = bounds;
        foreach ((int X, int Y) cell in Cells(bounds))
        {
            if (!cells.TryGetValue(cell, out List<T>? list))
            {
                cells[cell] = list = [];
            }

            list.Add(item);
        }
    }

    public bool Remove(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!items.Remove(item, out Area bounds))
        {
            return false;
        }

        RemoveFromCells(item, bounds);
        return true;
    }

    public void Clear()
    {
        cells.Clear();
        items.Clear();
    }

    /// <summary>The bounds <paramref name="item"/> was added with, or null when it is not in the index.</summary>
    public Area? BoundsOf(T item) => items.TryGetValue(item, out Area bounds) ? bounds : null;

    /// <summary>Every item whose bounds intersect <paramref name="area"/>, each once, in no particular order.</summary>
    public IReadOnlyCollection<T> Query(Area area)
    {
        HashSet<T> found = [];
        foreach ((int X, int Y) cell in Cells(area, query: true))
        {
            if (cells.TryGetValue(cell, out List<T>? list))
            {
                foreach (T item in list)
                {
                    if (!found.Contains(item) && items[item].Intersects(area))
                    {
                        found.Add(item);
                    }
                }
            }
        }

        return found;
    }

    private void RemoveFromCells(T item, Area bounds)
    {
        foreach ((int X, int Y) cell in Cells(bounds))
        {
            if (cells.TryGetValue(cell, out List<T>? list))
            {
                list.Remove(item);
                if (list.Count == 0)
                {
                    cells.Remove(cell);
                }
            }
        }
    }

    // The cells a rectangle overlaps. For a query, a rectangle larger than the cells in use is narrowed to those cells, so querying
    // the whole canvas zoomed far out stays cheap.
    private IEnumerable<(int X, int Y)> Cells(Area bounds, bool query = false)
    {
        int left = Cell(bounds.X), top = Cell(bounds.Y), right = Cell(bounds.Right), bottom = Cell(bounds.Bottom);
        if (query && (long)(right - left + 1) * (bottom - top + 1) > cells.Count * 4L + 64)
        {
            return cells.Keys.Where(cell => cell.X >= left && cell.X <= right && cell.Y >= top && cell.Y <= bottom).ToArray();
        }

        return Enumerate(left, top, right, bottom);
    }

    private static IEnumerable<(int X, int Y)> Enumerate(int left, int top, int right, int bottom)
    {
        for (int x = left; x <= right; x++)
        {
            for (int y = top; y <= bottom; y++)
            {
                yield return (x, y);
            }
        }
    }

    private int Cell(double value) => (int)Math.Clamp(Math.Floor(value / cellSize), int.MinValue / 2, int.MaxValue / 2);
}
