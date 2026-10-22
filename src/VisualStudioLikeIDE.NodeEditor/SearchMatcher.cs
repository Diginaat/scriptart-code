namespace VisualStudioLikeIDE.NodeEditor;

/// <summary>Ranks text against a search typed into a list filter, such as the command palette.</summary>
public static class SearchMatcher
{
    /// <summary>
    /// Returns null when <paramref name="text"/> does not match, otherwise a rank where lower is better:
    /// 0 when the text starts with the whole query, 1 when every word of the query starts a word of the text,
    /// 2 when every word of the query appears somewhere in the text. Matching ignores case. An empty query matches with rank 0.
    /// </summary>
    public static int? Rank(string text, string? query)
    {
        ArgumentNullException.ThrowIfNull(text);
        string trimmed = query?.Trim() ?? "";
        if (trimmed.Length == 0 || text.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        string[] terms = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!terms.All(term => text.Contains(term, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return terms.All(term => StartsWord(text, term)) ? 1 : 2;
    }

    private static bool StartsWord(string text, string term)
    {
        for (int index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase); index >= 0;
             index = text.IndexOf(term, index + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (index == 0 || !char.IsLetterOrDigit(text[index - 1]))
            {
                return true;
            }
        }

        return false;
    }
}
