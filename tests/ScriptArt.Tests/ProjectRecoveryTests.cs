using ScriptArt.Compiler.Emit;
using ScriptArt.ProjectSystem;

namespace ScriptArt.Tests;

/// <summary>Recovery copies written by autosave: kept outside the project, offered when newer, restored only on request (D-045).</summary>
public sealed class ProjectRecoveryTests
{
    [Fact]
    public void UnsavedGraphsAreKeptOutsideTheProjectAndRestored()
    {
        using var directory = new TemporaryDirectory();
        string projectFolder = Path.Combine(directory.Path, "Hello");
        Directory.CreateDirectory(projectFolder);
        VisualProject project = VisualProject.Create(projectFolder, "Hello");
        string graphPath = Path.GetFullPath(Path.Combine(projectFolder, project.Graphs[0].RelativePath));
        string savedGraph = File.ReadAllText(graphPath);
        var recovery = new ProjectRecovery(Path.Combine(directory.Path, "recovery"), project.ProjectFilePath);

        project.Graphs[0].Graph.Name = "Renamed";
        recovery.Write(project.SerializedFiles());

        Assert.Equal(savedGraph, File.ReadAllText(graphPath));
        Assert.StartsWith(Path.Combine(directory.Path, "recovery"), recovery.Folder, StringComparison.Ordinal);
        GeneratedFileChange change = Assert.Single(recovery.Changes());
        Assert.Equal(graphPath, change.Path);
        Assert.Equal(GeneratedFileChangeKind.Modified, change.Kind);
        Assert.Contains("Renamed", change.NewText, StringComparison.Ordinal);

        recovery.Restore();

        Assert.Equal("Renamed", VisualProject.Load(project.ProjectFilePath).Graphs[0].Graph.Name);
        Assert.False(Directory.Exists(recovery.Folder));
        Assert.Empty(recovery.Changes());
    }

    [Fact]
    public void NothingIsKeptWhenEverythingIsSaved()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "Hello");
        var recovery = new ProjectRecovery(Path.Combine(directory.Path, ".recovery"), project.ProjectFilePath);
        string notes = Path.Combine(directory.Path, "notes.md");

        recovery.Write(new Dictionary<string, string> { [notes] = "draft" });
        Assert.True(Directory.Exists(recovery.Folder));
        Assert.Equal(GeneratedFileChangeKind.Added, Assert.Single(recovery.Changes()).Kind);

        recovery.Write(project.SerializedFiles());

        Assert.False(Directory.Exists(recovery.Folder));
    }

    [Fact]
    public void FilesSavedAfterTheCopyWithConflictingChangesAreNotOffered()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "Hello");
        string code = Path.Combine(directory.Path, "Helpers.cs");
        File.WriteAllText(code, "class Helpers {}");
        var recovery = new ProjectRecovery(Path.Combine(directory.Path, ".recovery"), project.ProjectFilePath);

        recovery.Write(new Dictionary<string, string> { [code] = "class Helpers { int x; }" });
        File.WriteAllText(code, "class Helpers { int y; }");
        File.SetLastWriteTimeUtc(code, DateTime.UtcNow.AddMinutes(1));

        Assert.Empty(recovery.Changes());
    }

    [Fact]
    public void FilesSavedAfterTheCopyAreMergedWithTheRecoveredWork()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "Hello");
        string code = Path.Combine(directory.Path, "Helpers.cs");
        File.WriteAllText(code, "class Helpers\n{\n    int a;\n\n    int b;\n}\n");
        var recovery = new ProjectRecovery(Path.Combine(directory.Path, ".recovery"), project.ProjectFilePath);

        recovery.Write(new Dictionary<string, string> { [code] = "class Helpers\n{\n    int a = 1;\n\n    int b;\n}\n" });
        File.WriteAllText(code, "class Helpers\n{\n    int a;\n\n    int b = 2;\n}\n");
        File.SetLastWriteTimeUtc(code, DateTime.UtcNow.AddMinutes(1));

        GeneratedFileChange change = Assert.Single(recovery.Changes());
        Assert.Equal("class Helpers\n{\n    int a = 1;\n\n    int b = 2;\n}\n", change.NewText);
        recovery.Restore();
        Assert.Equal(change.NewText, File.ReadAllText(code));
    }

    [Fact]
    public void AMergedGraphThatNoLongerReadsIsNotOffered()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "Hello");
        string graphPath = Path.GetFullPath(Path.Combine(directory.Path, project.Graphs[0].RelativePath));
        string saved = File.ReadAllText(graphPath);
        var recovery = new ProjectRecovery(Path.Combine(directory.Path, ".recovery"), project.ProjectFilePath);

        project.Graphs[0].Graph.Name = "Renamed";
        recovery.Write(project.SerializedFiles());
        File.WriteAllText(graphPath, saved.TrimEnd() + "\n// not JSON\n");
        File.SetLastWriteTimeUtc(graphPath, DateTime.UtcNow.AddMinutes(1));

        Assert.Empty(recovery.Changes());
    }

    [Fact]
    public void PathsOutsideTheProjectAndGeneratedTextAreRefused()
    {
        using var directory = new TemporaryDirectory();
        string projectFolder = Path.Combine(directory.Path, "Hello");
        Directory.CreateDirectory(projectFolder);
        VisualProject project = VisualProject.Create(projectFolder, "Hello");
        var recovery = new ProjectRecovery(Path.Combine(directory.Path, "recovery"), project.ProjectFilePath);

        Assert.Throws<ArgumentException>(() => recovery.Write(new Dictionary<string, string> { [Path.Combine(directory.Path, "outside.cs")] = "x" }));
        Assert.Throws<InvalidOperationException>(() => recovery.Write(new Dictionary<string, string>
        {
            [Path.Combine(projectFolder, "Generated", "X.g.cs")] = "// <auto-generated>\n" + GeneratedSource.OwnershipMarker + "\n",
        }));

        // A manifest edited to point outside the project is ignored, not followed.
        recovery.Write(new Dictionary<string, string> { [Path.Combine(projectFolder, "a.txt")] = "a" });
        string manifest = Path.Combine(recovery.Folder, "manifest.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"a.txt\"", "\"../../evil.txt\"", StringComparison.Ordinal));
        Directory.CreateDirectory(Path.Combine(recovery.Folder, "evil"));
        Assert.Empty(recovery.Changes());
        recovery.Restore();
        Assert.False(File.Exists(Path.Combine(directory.Path, "evil.txt")));
    }

    [Fact]
    public void EachProjectHasItsOwnFolder()
    {
        using var directory = new TemporaryDirectory();
        string root = Path.Combine(directory.Path, "recovery");

        Assert.NotEqual(new ProjectRecovery(root, Path.Combine(directory.Path, "A", "A.vside")).Folder, new ProjectRecovery(root, Path.Combine(directory.Path, "B", "B.vside")).Folder);
        Assert.Equal(new ProjectRecovery(root, Path.Combine(directory.Path, "A", "A.vside")).Folder, new ProjectRecovery(root, Path.Combine(directory.Path, "A", ".", "A.vside")).Folder);
    }
}
