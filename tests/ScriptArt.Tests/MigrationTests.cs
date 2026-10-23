using ScriptArt.GraphModel;
using ScriptArt.ProjectSystem;

namespace ScriptArt.Tests;

public sealed class MigrationTests
{
    private const string Version1Graph = """{ "schemaVersion": 1, "id": "g", "name": "Program", "nodes": [], "connections": [] }""";

    [Fact]
    public void EveryVersionAfterTheFirstHasOneStepInOrder()
    {
        Assert.Equal(Enumerable.Range(2, GraphDocument.CurrentSchemaVersion - 1), GraphSerializer.Migrations.Select(step => step.ToVersion));
        Assert.All(GraphSerializer.Migrations, step => Assert.False(string.IsNullOrWhiteSpace(step.Description)));
    }

    [Fact]
    public void MigrateRewritesOutdatedFilesAfterBackingThemUp()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "Hello");
        string graphPath = Path.GetFullPath(Path.Combine(directory.Path, project.Document.Graphs[0]));
        File.WriteAllText(graphPath, Version1Graph);

        FileSchemaVersion outdated = Assert.Single(ProjectMigration.Inspect(project.ProjectFilePath), file => file.IsOutdated);
        Assert.Equal((graphPath, 1), (outdated.Path, outdated.Version));
        Assert.Equal(GraphDocument.CurrentSchemaVersion - 1, ProjectMigration.GraphStepsFrom(1).Count());

        Assert.Equal([outdated], ProjectMigration.Migrate(project.ProjectFilePath));

        Assert.DoesNotContain(ProjectMigration.Inspect(project.ProjectFilePath), file => file.IsOutdated);
        Assert.Equal("Program", GraphSerializer.Load(graphPath).Name);
        string backup = Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "MigrationBackup"), "*.graph.json", SearchOption.AllDirectories));
        Assert.Equal(Version1Graph, File.ReadAllText(backup));
        Assert.Empty(ProjectMigration.Migrate(project.ProjectFilePath));
    }

    [Fact]
    public void AnUnreadableFileLeavesEveryFileUnchanged()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "Hello");
        project.AddFunction("Helper");
        project.Save();
        string first = Path.Combine(directory.Path, project.Document.Graphs[0]);
        string second = Path.Combine(directory.Path, project.Document.Graphs[1]);
        File.WriteAllText(first, Version1Graph);
        File.WriteAllText(second, """{ "schemaVersion": 2, "name": "No id" }""");

        Assert.Throws<GraphFormatException>(() => ProjectMigration.Migrate(project.ProjectFilePath));

        Assert.Equal(Version1Graph, File.ReadAllText(first));
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "MigrationBackup")));
    }

    [Fact]
    public void AFileFromANewerVersionIsReported()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "Hello");
        File.WriteAllText(Path.Combine(directory.Path, project.Document.Graphs[0]), """{ "schemaVersion": 999, "id": "g", "name": "P" }""");

        Assert.Contains("999", Assert.Throws<GraphFormatException>(() => ProjectMigration.Inspect(project.ProjectFilePath)).Message, StringComparison.Ordinal);
    }
}
