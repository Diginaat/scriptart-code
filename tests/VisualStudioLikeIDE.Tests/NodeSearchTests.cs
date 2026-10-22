using VisualStudioLikeIDE.Compiler.Nodes;
using VisualStudioLikeIDE.NodeEditor;

namespace VisualStudioLikeIDE.Tests;

/// <summary>Ranking nodes in the palette and the action menu.</summary>
public sealed class NodeSearchTests
{
    private static readonly NodeLibrary Library = NodeLibrary.CreateDefault();

    private static string First(string query) => NodeSearch.Filter(Library.Definitions, query).First().DisplayName;

    [Fact]
    public void NamesComeBeforeDescriptionsAndSeveralWordsMatchInAnyOrder()
    {
        Assert.Equal("Print Line", First("print line"));
        Assert.Equal("Print Line", First("line print"));
        Assert.Equal("Concatenate", First("concat"));
    }

    [Fact]
    public void WordsFromOtherLanguagesFindTheNodeTheyMean()
    {
        Assert.Equal("Branch", First("if"));
        Assert.Contains(NodeSearch.Filter(Library.Definitions, "loop"), definition => definition.DisplayName.StartsWith("For Loop", StringComparison.Ordinal));
    }

    [Fact]
    public void TyposAndInitialsStillMatch()
    {
        Assert.Equal("Concatenate", First("conact"));
        Assert.Equal("Print Line", First("pl"));
        Assert.Empty(NodeSearch.Filter(Library.Definitions, "zzzz"));
    }

    [Fact]
    public void EmptySearchListsBuiltInNodesFirstInCategoryOrder()
    {
        NodeDefinition[] all = [.. NodeSearch.Filter(Library.Definitions, "")];
        NodeDefinition[] builtIn = [.. all.TakeWhile(definition => !definition.TypeId.StartsWith(ExternalApi.TypeIdPrefix, StringComparison.Ordinal))];

        Assert.Equal(Library.Definitions.Count(), all.Length);
        Assert.All(all.Skip(builtIn.Length), definition => Assert.StartsWith(ExternalApi.TypeIdPrefix, definition.TypeId, StringComparison.Ordinal));
        Assert.Equal(builtIn.Select(definition => definition.Category).Order(StringComparer.Ordinal), builtIn.Select(definition => definition.Category));
    }
}
