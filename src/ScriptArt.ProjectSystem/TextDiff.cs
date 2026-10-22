using System.Globalization;
using System.Text;

namespace ScriptArt.ProjectSystem;

public enum DiffKind
{
    Same,
    Removed,
    Added,
}

/// <summary>One line of a line diff. <paramref name="OldLine"/> and <paramref name="NewLine"/> are one-based, and null on the side the line is not on.</summary>
public sealed record DiffLine(DiffKind Kind, string Text, int? OldLine, int? NewLine);

/// <summary>
/// Line diffs of text files with Myers' algorithm, used to preview what generation would change. Line endings are normalized to
/// <c>\n</c> first, so only content differences show.
/// </summary>
public static class TextDiff
{
    // Beyond this many differing lines the edit path is not searched; the changed middle shows as removed, then added.
    private const int MaxEditDistance = 1000;

    /// <summary>Every line of <paramref name="oldText"/> and <paramref name="newText"/>, in order, marked as kept, removed, or added.</summary>
    public static IReadOnlyList<DiffLine> Lines(string oldText, string newText)
    {
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        string[] a = SplitLines(oldText);
        string[] b = SplitLines(newText);

        int prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix])
        {
            suffix++;
        }

        List<DiffLine> result = [];
        for (int index = 0; index < prefix; index++)
        {
            result.Add(new DiffLine(DiffKind.Same, a[index], index + 1, index + 1));
        }

        AddMiddle(result, a, b, prefix, a.Length - suffix, b.Length - suffix);
        for (int index = suffix; index > 0; index--)
        {
            result.Add(new DiffLine(DiffKind.Same, a[a.Length - index], a.Length - index + 1, b.Length - index + 1));
        }

        return result;
    }

    /// <summary>
    /// A unified diff (<c>---</c>/<c>+++</c> headers and <c>@@</c> hunks with <paramref name="context"/> lines around each change), as
    /// <c>git diff</c> prints it. Empty when the texts have the same lines.
    /// </summary>
    public static string Unified(string oldName, string newName, string oldText, string newText, int context = 3)
    {
        IReadOnlyList<DiffLine> lines = Lines(oldText, newText);
        List<int> changed = [.. Enumerable.Range(0, lines.Count).Where(index => lines[index].Kind != DiffKind.Same)];
        if (changed.Count == 0)
        {
            return "";
        }

        var text = new StringBuilder();
        text.Append("--- ").Append(oldName).Append('\n');
        text.Append("+++ ").Append(newName).Append('\n');
        int next = 0;
        while (next < changed.Count)
        {
            int start = Math.Max(0, changed[next] - context);
            int last = next;
            while (last + 1 < changed.Count && changed[last + 1] - changed[last] <= 2 * context)
            {
                last++;
            }

            int end = Math.Min(lines.Count - 1, changed[last] + context);
            int oldBefore = lines.Take(start).Count(line => line.Kind != DiffKind.Added);
            int newBefore = lines.Take(start).Count(line => line.Kind != DiffKind.Removed);
            DiffLine[] hunk = [.. lines.Skip(start).Take(end - start + 1)];
            int oldCount = hunk.Count(line => line.Kind != DiffKind.Added);
            int newCount = hunk.Count(line => line.Kind != DiffKind.Removed);
            text.Append(CultureInfo.InvariantCulture, $"@@ -{Range(oldBefore, oldCount)} +{Range(newBefore, newCount)} @@\n");
            foreach (DiffLine line in hunk)
            {
                text.Append(line.Kind switch { DiffKind.Added => '+', DiffKind.Removed => '-', _ => ' ' }).Append(line.Text).Append('\n');
            }

            next = last + 1;
        }

        return text.ToString();
    }

    // An empty range names the line before it, as in git.
    private static string Range(int before, int count) =>
        count == 1 ? (before + 1).ToString(CultureInfo.InvariantCulture)
        : count == 0 ? $"{before.ToString(CultureInfo.InvariantCulture)},0"
        : $"{(before + 1).ToString(CultureInfo.InvariantCulture)},{count.ToString(CultureInfo.InvariantCulture)}";

    private static string[] SplitLines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        string[] lines = text.ReplaceLineEndings("\n").Split('\n');
        return lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    /// <summary>Appends the diff of <c>a[start..oldEnd]</c> and <c>b[start..newEnd]</c>, found with Myers' greedy algorithm.</summary>
    private static void AddMiddle(List<DiffLine> result, string[] a, string[] b, int start, int oldEnd, int newEnd)
    {
        int n = oldEnd - start;
        int m = newEnd - start;
        int max = Math.Min(n + m, MaxEditDistance);
        int offset = max + 1;
        var v = new int[(2 * max) + 3];
        List<int[]> trace = [];
        int found = -1;
        for (int d = 0; d <= max && found < 0; d++)
        {
            trace.Add((int[])v.Clone());
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]) ? v[offset + k + 1] : v[offset + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && a[start + x] == b[start + y])
                {
                    x++;
                    y++;
                }

                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    found = d;
                    break;
                }
            }
        }

        if (found < 0)
        {
            for (int index = 0; index < n; index++)
            {
                result.Add(new DiffLine(DiffKind.Removed, a[start + index], start + index + 1, null));
            }

            for (int index = 0; index < m; index++)
            {
                result.Add(new DiffLine(DiffKind.Added, b[start + index], null, start + index + 1));
            }

            return;
        }

        // Walk the trace back from the end, collecting lines in reverse.
        List<DiffLine> reversed = [];
        int cx = n;
        int cy = m;
        for (int d = found; d > 0; d--)
        {
            int[] previous = trace[d];
            int k = cx - cy;
            int previousK = k == -d || (k != d && previous[offset + k - 1] < previous[offset + k + 1]) ? k + 1 : k - 1;
            int previousX = previous[offset + previousK];
            int previousY = previousX - previousK;
            while (cx > previousX && cy > previousY)
            {
                cx--;
                cy--;
                reversed.Add(new DiffLine(DiffKind.Same, a[start + cx], start + cx + 1, start + cy + 1));
            }

            if (cx == previousX)
            {
                cy--;
                reversed.Add(new DiffLine(DiffKind.Added, b[start + cy], null, start + cy + 1));
            }
            else
            {
                cx--;
                reversed.Add(new DiffLine(DiffKind.Removed, a[start + cx], start + cx + 1, null));
            }
        }

        while (cx > 0 && cy > 0)
        {
            cx--;
            cy--;
            reversed.Add(new DiffLine(DiffKind.Same, a[start + cx], start + cx + 1, start + cy + 1));
        }

        reversed.Reverse();
        result.AddRange(reversed);
    }
}
