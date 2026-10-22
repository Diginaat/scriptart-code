using VisualStudioLikeIDE.Compiler.Nodes;

namespace VisualStudioLikeIDE.NodeEditor;

/// <summary>
/// Ranks nodes against a search typed into the palette or the action menu, so the node a user means comes first even when they
/// type a programming word from another language ("if", "print"), part of the description, or a name with a typo.
/// </summary>
public static class NodeSearch
{
    // Words people type for a node whose name uses other words. Each key adds the listed words to the terms a name may match.
    private static readonly Dictionary<string, string[]> Synonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["if"] = ["branch"],
        ["else"] = ["branch"],
        ["condition"] = ["branch"],
        ["print"] = ["write"],
        ["log"] = ["write"],
        ["output"] = ["write"],
        ["echo"] = ["write"],
        ["input"] = ["read"],
        ["ask"] = ["read"],
        ["loop"] = ["for", "while", "each"],
        ["repeat"] = ["for", "while"],
        ["foreach"] = ["for each"],
        ["iterate"] = ["for each"],
        ["concat"] = ["concatenate", "join"],
        ["combine"] = ["concatenate", "join"],
        ["append"] = ["add", "concatenate"],
        ["push"] = ["add"],
        ["delete"] = ["remove"],
        ["array"] = ["list"],
        ["map"] = ["dictionary"],
        ["hashmap"] = ["dictionary"],
        ["lookup"] = ["dictionary", "get value"],
        ["length"] = ["count"],
        ["size"] = ["count"],
        ["sum"] = ["add"],
        ["plus"] = ["add"],
        ["minus"] = ["subtract"],
        ["times"] = ["multiply"],
        ["mod"] = ["remainder"],
        ["modulo"] = ["remainder"],
        ["equals"] = ["equal"],
        ["parse"] = ["to int", "to number", "convert"],
        ["tostring"] = ["to string", "to text"],
        ["catch"] = ["try"],
        ["exception"] = ["try", "throw", "error"],
        ["error"] = ["throw", "try"],
        ["create"] = ["new"],
        ["make"] = ["new", "create"],
        ["this"] = ["self"],
        ["var"] = ["get", "set"],
        ["variable"] = ["get", "set"],
        ["assign"] = ["set"],
        ["wait"] = ["delay", "sleep"],
        ["pause"] = ["sleep", "delay"],
    };

    /// <summary>
    /// Null when <paramref name="definition"/> does not match <paramref name="query"/>, otherwise a rank where lower is better:
    /// 0 or 1 when the name starts with the query or its words (see <see cref="SearchMatcher.Rank"/>), 2 for words that start words of
    /// the name directly or through a synonym, or the name's initials, 3 for a name containing the query mid-word, 4 for the category,
    /// 5 for the description, and 6 for a name that matches with one typo per word. An empty query matches with rank 0.
    /// </summary>
    public static int? Rank(NodeDefinition definition, string? query)
    {
        ArgumentNullException.ThrowIfNull(definition);
        string trimmed = query?.Trim() ?? "";
        int? nameRank = SearchMatcher.Rank(definition.DisplayName, trimmed);
        if (nameRank is 0 or 1)
        {
            return nameRank;
        }

        // A synonym that starts a word of the name beats letters found in the middle of a word ("if" inside "Qualified").
        string[] terms = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.All(term => SearchMatcher.Rank(definition.DisplayName, term) is 0 or 1
            || (Synonyms.TryGetValue(term, out string[]? alternatives) && alternatives.Any(alternative => SearchMatcher.Rank(definition.DisplayName, alternative) is 0 or 1)))
            || IsInitials(definition.DisplayName, trimmed))
        {
            return 2;
        }

        if (nameRank is not null)
        {
            return 3;
        }

        if (SearchMatcher.Rank(definition.Category, trimmed) is not null)
        {
            return 4;
        }

        if (terms.All(term => definition.Description.Contains(term, StringComparison.OrdinalIgnoreCase)))
        {
            return 5;
        }

        return terms.All(term => NearWord(definition.DisplayName, term)) ? 6 : null;
    }

    /// <summary>
    /// The definitions that match <paramref name="query"/>, best first; the built-in nodes come before .NET members among equally good
    /// matches, then category and name order.
    /// </summary>
    public static IEnumerable<NodeDefinition> Filter(IEnumerable<NodeDefinition> definitions, string? query)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        return definitions
            .Select(definition => (definition, rank: Rank(definition, query)))
            .Where(match => match.rank is not null)
            .OrderBy(match => match.rank)
            .ThenBy(match => match.definition.TypeId.StartsWith(ExternalApi.TypeIdPrefix, StringComparison.Ordinal))
            .ThenBy(match => match.definition.Category, StringComparer.Ordinal)
            .ThenBy(match => match.definition.DisplayName, StringComparer.Ordinal)
            .Select(match => match.definition);
    }

    // "pl" for Print Line: the query's letters are the first letters of the name's words, in order.
    private static bool IsInitials(string name, string query)
    {
        string initials = string.Concat(name.Split([' ', '.', '(', ')', '<', '>'], StringSplitOptions.RemoveEmptyEntries).Select(word => word[0]));
        return query.Length >= 2 && !query.Contains(' ', StringComparison.Ordinal) && initials.StartsWith(query, StringComparison.OrdinalIgnoreCase);
    }

    // A term of four or more letters matches a word of the name it differs from by one letter: "conact" finds Concatenate.
    private static bool NearWord(string name, string term) =>
        term.Length >= 4 && name.Split([' ', '.', '(', ')', '<', '>'], StringSplitOptions.RemoveEmptyEntries)
            .Any(word => WithinOneEdit(word.Length > term.Length + 1 ? word[..(term.Length + 1)] : word, term)
                || WithinOneEdit(word.Length > term.Length ? word[..term.Length] : word, term));

    // True when a and b differ by at most one inserted, removed, replaced, or swapped letter, ignoring case.
    private static bool WithinOneEdit(string a, string b)
    {
        a = a.ToUpperInvariant();
        b = b.ToUpperInvariant();
        if (Math.Abs(a.Length - b.Length) > 1)
        {
            return false;
        }

        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i])
        {
            i++;
        }

        if (i == a.Length && i == b.Length)
        {
            return true;
        }

        return a.Length == b.Length
            ? a[(i + 1)..] == b[(i + 1)..] || (i + 1 < a.Length && a[i] == b[i + 1] && a[i + 1] == b[i] && a[(i + 2)..] == b[(i + 2)..])
            : a.Length > b.Length ? a[(i + 1)..] == b[i..] : a[i..] == b[(i + 1)..];
    }
}
