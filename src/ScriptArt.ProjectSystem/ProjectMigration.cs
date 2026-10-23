using System.Globalization;
using System.Text.Json.Nodes;
using ScriptArt.GraphModel;

namespace ScriptArt.ProjectSystem;

/// <summary>A project or graph file and the schema version it is stored in.</summary>
/// <param name="Path">The full path of the file.</param>
/// <param name="CurrentVersion">The version this build writes. The file is outdated when its version is lower.</param>
public sealed record FileSchemaVersion(string Path, int Version, int CurrentVersion)
{
    public bool IsOutdated => Version < CurrentVersion;
}

/// <summary>
/// Reports and upgrades the schema versions of a project's files. Loading already upgrades files in memory; migrating writes them
/// in the current format, after copying the originals to a backup folder, so a project can be upgraded in one step (for example in
/// a repository) rather than file by file as graphs are saved.
/// </summary>
public static class ProjectMigration
{
    /// <summary>The schema version of the project file and of every graph file it lists, read without loading the graphs.</summary>
    public static IReadOnlyList<FileSchemaVersion> Inspect(string projectFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFilePath);
        string projectPath = Path.GetFullPath(projectFilePath);
        string directory = Path.GetDirectoryName(projectPath)!;
        List<FileSchemaVersion> files = [new(projectPath, ReadVersion(projectPath, ProjectDocument.CurrentSchemaVersion, "project"), ProjectDocument.CurrentSchemaVersion)];
        ProjectDocument document = ProjectSerializer.Deserialize(File.ReadAllText(projectPath));
        foreach (string relative in document.Graphs)
        {
            string path = VisualProject.ResolveInside(directory, relative);
            files.Add(new(path, ReadVersion(path, GraphDocument.CurrentSchemaVersion, "graph"), GraphDocument.CurrentSchemaVersion));
        }

        return files;
    }

    /// <summary>
    /// Rewrites every outdated file of the project in the current format. Every outdated file is read and converted before any is
    /// written, so a file that cannot be read leaves the project unchanged. The originals are copied first to
    /// <c>MigrationBackup/{time}/</c> in the project folder, keeping their relative paths. Returns the files that were rewritten.
    /// </summary>
    public static IReadOnlyList<FileSchemaVersion> Migrate(string projectFilePath)
    {
        IReadOnlyList<FileSchemaVersion> outdated = [.. Inspect(projectFilePath).Where(file => file.IsOutdated)];
        if (outdated.Count == 0)
        {
            return [];
        }

        string projectPath = Path.GetFullPath(projectFilePath);
        string directory = Path.GetDirectoryName(projectPath)!;
        List<(FileSchemaVersion File, string Text)> converted = [.. outdated.Select(file => (file, Convert(file.Path, projectPath)))];

        string backup = Path.Combine(directory, "MigrationBackup", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        foreach ((FileSchemaVersion file, _) in converted)
        {
            string target = Path.Combine(backup, Path.GetRelativePath(directory, file.Path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file.Path, target, overwrite: false);
        }

        foreach ((FileSchemaVersion file, string text) in converted)
        {
            AtomicFile.WriteAllText(file.Path, text);
        }

        return outdated;
    }

    /// <summary>What migrating each step does, for the versions between <paramref name="from"/> and the current graph version.</summary>
    public static IEnumerable<SchemaMigration> GraphStepsFrom(int from) => GraphSerializer.Migrations.Where(step => step.ToVersion > from);

    private static string Convert(string path, string projectPath) =>
        string.Equals(path, projectPath, StringComparison.OrdinalIgnoreCase)
            ? ProjectSerializer.Serialize(ProjectSerializer.Deserialize(File.ReadAllText(path)))
            : GraphSerializer.Serialize(GraphSerializer.Load(path));

    private static int ReadVersion(string path, int currentVersion, string kind)
    {
        try
        {
            JsonObject root = GraphSerializer.ParseRoot(File.ReadAllText(path));
            return GraphSerializer.ReadSchemaVersion(root, currentVersion, kind);
        }
        catch (GraphFormatException exception)
        {
            throw new GraphFormatException($"{path}: {exception.Message}", exception);
        }
    }
}
