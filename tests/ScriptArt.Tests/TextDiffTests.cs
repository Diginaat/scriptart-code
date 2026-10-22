using ScriptArt.ProjectSystem;

namespace ScriptArt.Tests;

public sealed class TextDiffTests
{
    [Fact]
    public void EqualTextsHaveNoUnifiedDiff()
    {
        Assert.Empty(TextDiff.Unified("a", "b", "x\ny\n", "x\r\ny\r\n"));
        Assert.All(TextDiff.Lines("x\ny\n", "x\ny\n"), line => Assert.Equal(DiffKind.Same, line.Kind));
    }

    [Fact]
    public void ChangedLineIsRemovedThenAddedWithLineNumbers()
    {
        IReadOnlyList<DiffLine> lines = TextDiff.Lines("a\nb\nc\n", "a\nB\nc\nd\n");

        Assert.Equal(
            [
                new DiffLine(DiffKind.Same, "a", 1, 1),
                new DiffLine(DiffKind.Removed, "b", 2, null),
                new DiffLine(DiffKind.Added, "B", null, 2),
                new DiffLine(DiffKind.Same, "c", 3, 3),
                new DiffLine(DiffKind.Added, "d", null, 4),
            ],
            lines);
    }

    [Fact]
    public void UnifiedDiffGroupsNearbyChangesIntoHunksWithContext()
    {
        string[] old = [.. Enumerable.Range(1, 20).Select(number => $"line {number}")];
        string[] changed = [.. old];
        changed[1] = "changed 2";
        changed[17] = "changed 18";

        string diff = TextDiff.Unified("a/f.cs", "b/f.cs", string.Join('\n', old) + "\n", string.Join('\n', changed) + "\n", context: 2);

        Assert.Equal(
            """
            --- a/f.cs
            +++ b/f.cs
            @@ -1,4 +1,4 @@
             line 1
            -line 2
            +changed 2
             line 3
             line 4
            @@ -16,5 +16,5 @@
             line 16
             line 17
            -line 18
            +changed 18
             line 19
             line 20

            """.ReplaceLineEndings("\n"),
            diff);
    }

    [Fact]
    public void AddedAndDeletedFilesDiffAgainstNothing()
    {
        Assert.Equal("--- /dev/null\n+++ b/x\n@@ -0,0 +1,2 @@\n+one\n+two\n",
            new GeneratedFileChange("x", GeneratedFileChangeKind.Added, "", "one\ntwo\n").UnifiedDiff("x"));
        Assert.Equal("--- a/x\n+++ /dev/null\n@@ -1 +0,0 @@\n-one\n",
            new GeneratedFileChange("x", GeneratedFileChangeKind.Deleted, "one\n", "").UnifiedDiff("x"));
    }

    [Fact]
    public void LargeRewritesStillDiffEveryLine()
    {
        string old = string.Join('\n', Enumerable.Range(0, 3_000).Select(number => $"old {number}"));
        string changed = string.Join('\n', Enumerable.Range(0, 3_000).Select(number => $"new {number}"));

        IReadOnlyList<DiffLine> lines = TextDiff.Lines(old, changed);

        Assert.Equal(3_000, lines.Count(line => line.Kind == DiffKind.Removed));
        Assert.Equal(3_000, lines.Count(line => line.Kind == DiffKind.Added));
    }
}
