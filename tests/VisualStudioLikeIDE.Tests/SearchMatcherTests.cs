using VisualStudioLikeIDE.NodeEditor;

namespace VisualStudioLikeIDE.Tests;

public sealed class SearchMatcherTests
{
    [Theory]
    [InlineData("Edit: Undo", "", 0)]
    [InlineData("Edit: Undo", "  ", 0)]
    [InlineData("Edit: Undo", "edit", 0)]
    [InlineData("Edit: Arrange › Align Left", "align left", 1)]
    [InlineData("Edit: Arrange › Align Left", "left ali", 1)]
    [InlineData("Add Node: Print Line", "rint", 2)]
    [InlineData("Add Node: Print Line", "node ine", 2)]
    public void RanksMatches(string text, string query, int expected) =>
        Assert.Equal(expected, SearchMatcher.Rank(text, query));

    [Theory]
    [InlineData("Edit: Undo", "redo")]
    [InlineData("Add Node: Print Line", "print zebra")]
    public void RejectsTextMissingAWord(string text, string query) =>
        Assert.Null(SearchMatcher.Rank(text, query));

    [Fact]
    public void WordStartIsFoundAfterAnEarlierInnerMatch()
    {
        // "print" first occurs inside "Sprint"; the second occurrence starts a word.
        Assert.Equal(1, SearchMatcher.Rank("Sprint Print", "print"));
    }
}
