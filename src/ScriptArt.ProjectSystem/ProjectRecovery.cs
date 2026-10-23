using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScriptArt.Compiler.Emit;
using ScriptArt.GraphModel;

namespace ScriptArt.ProjectSystem;

/// <summary>
/// Recovery copies of a project's unsaved work, written by autosave outside the project (D-045):
/// <c>recovery/&lt;project hash&gt;/</c> under the local application data folder holds <c>manifest.json</c> and a copy of each
/// unsaved file at its path relative to the project folder, with the file as it was on disk then under <c>base/</c>. Autosave never
/// writes the project's own files; only <see cref="Restore"/>, which the user chooses after seeing the differences, does.
/// </summary>
public sealed class ProjectRecovery
{
    public const int CurrentSchemaVersion = 1;

    private const string ManifestName = "manifest.json";
    private const string FilesFolder = "files";
    private const string BaseFolder = "base";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, NewLine = "\n" };

    public ProjectRecovery(string root, string projectFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFilePath);
        ProjectFilePath = Path.GetFullPath(projectFilePath);
        ProjectDirectory = Path.GetDirectoryName(ProjectFilePath)!;
        string key = OperatingSystem.IsWindows() ? ProjectFilePath.ToUpperInvariant() : ProjectFilePath;
        Folder = Path.Combine(root, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16]);
    }

    /// <summary>Where recovery copies are kept for this user: <c>recovery</c> in the IDE's local application data folder.</summary>
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScriptArtCode", "recovery");

    public string ProjectFilePath { get; }

    public string ProjectDirectory { get; }

    /// <summary>This project's recovery folder.</summary>
    public string Folder { get; }

    /// <summary>
    /// Replaces the recovery copies with <paramref name="files"/> (full path to text), keeping only those that differ from the file on
    /// disk. With nothing left to keep, the recovery folder is removed. Files outside the project folder and generated files are
    /// refused.
    /// </summary>
    public void Write(IReadOnlyDictionary<string, string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        List<(string Relative, string Text, string? Base)> changed = [];
        foreach ((string path, string text) in files)
        {
            string relative = Relative(path);
            string fullPath = Path.Combine(ProjectDirectory, relative);
            if (GeneratedSource.IsGeneratedText(text))
            {
                throw new InvalidOperationException($"'{relative}' is a generated file; it is not kept as a recovery copy.");
            }

            string? onDisk = File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
            if (onDisk != text)
            {
                changed.Add((relative, text, onDisk));
            }
        }

        if (changed.Count == 0)
        {
            Clear();
            return;
        }

        HashSet<string> kept = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach ((string relative, string text, string? onDisk) in changed)
        {
            kept.Add(Write(Path.Combine(Folder, FilesFolder, relative), text));
            if (onDisk is not null)
            {
                kept.Add(Write(Path.Combine(Folder, BaseFolder, relative), onDisk));
            }
        }

        var manifest = new JsonObject
        {
            ["schemaVersion"] = CurrentSchemaVersion,
            ["projectFilePath"] = ProjectFilePath,
            ["writtenAt"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["files"] = new JsonArray([.. changed.Select(file => (JsonNode)JsonValue.Create(file.Relative.Replace('\\', '/')))]),
        };
        AtomicFile.WriteAllText(Path.Combine(Folder, ManifestName), manifest.ToJsonString(WriteOptions) + "\n");

        // Copies of files that are saved by now are no longer needed.
        foreach (string folder in new[] { FilesFolder, BaseFolder }.Select(name => Path.Combine(Folder, name)).Where(Directory.Exists))
        {
            foreach (string stale in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(path => !kept.Contains(path)).ToArray())
            {
                File.Delete(stale);
            }
        }

        static string Write(string path, string text)
        {
            AtomicFile.WriteAllText(path, text);
            return Path.GetFullPath(path);
        }
    }

    /// <summary>Removes the recovery copies, after a save or when the user discards them.</summary>
    public void Clear()
    {
        if (Directory.Exists(Folder))
        {
            Directory.Delete(Folder, recursive: true);
        }
    }

    /// <summary>
    /// The recovered files that differ from the project's files, as changes from the file on disk to the text restoring writes. A file
    /// saved after the copy was made (by the IDE or another tool) is merged line by line with the recovered work; when both changed
    /// the same lines, or the merged project or graph file no longer reads, the saved file wins and the copy is not offered. A missing,
    /// unreadable, or foreign manifest gives none.
    /// </summary>
    public IReadOnlyList<GeneratedFileChange> Changes()
    {
        if (ReadManifest() is not (DateTime writtenAt, IReadOnlyList<string> files))
        {
            return [];
        }

        List<GeneratedFileChange> changes = [];
        foreach (string relative in files)
        {
            string copy = Path.Combine(Folder, FilesFolder, relative);
            if (!File.Exists(copy))
            {
                continue;
            }

            string target = Path.Combine(ProjectDirectory, relative);
            string recovered = File.ReadAllText(copy);
            if (!File.Exists(target))
            {
                changes.Add(new GeneratedFileChange(target, GeneratedFileChangeKind.Added, "", recovered));
                continue;
            }

            string current = File.ReadAllText(target);
            if (current == recovered || GeneratedSource.IsGeneratedText(current))
            {
                continue;
            }

            string? restored = File.GetLastWriteTimeUtc(target) <= writtenAt ? recovered : Merged(relative, recovered, current);
            if (restored is not null && restored != current)
            {
                changes.Add(new GeneratedFileChange(target, GeneratedFileChangeKind.Modified, current, restored));
            }
        }

        return changes;
    }

    /// <summary>Writes the recovered text of <see cref="Changes"/> over the project's files, then removes the recovery copies.</summary>
    public void Restore()
    {
        foreach (GeneratedFileChange change in Changes())
        {
            AtomicFile.WriteAllText(change.Path, change.NewText);
        }

        Clear();
    }

    // The recovered work merged into a file saved after the copy; null on a conflict, without the file as it was then, or when the
    // merged project or graph file does not read.
    private string? Merged(string relative, string recovered, string current)
    {
        string basePath = Path.Combine(Folder, BaseFolder, relative);
        if (!File.Exists(basePath) || TextDiff.Merge(File.ReadAllText(basePath), recovered, current) is not { } merged)
        {
            return null;
        }

        try
        {
            if (relative.EndsWith(GraphSerializer.FileExtension, StringComparison.OrdinalIgnoreCase))
            {
                GraphSerializer.Deserialize(merged);
            }
            else if (Path.GetFullPath(Path.Combine(ProjectDirectory, relative)) == ProjectFilePath)
            {
                ProjectSerializer.Deserialize(merged);
            }
        }
        catch (GraphFormatException)
        {
            return null;
        }

        return merged;
    }

    // The manifest's time and relative paths; null when there is none or it does not belong to this project.
    private (DateTime WrittenAt, IReadOnlyList<string> Files)? ReadManifest()
    {
        string path = Path.Combine(Folder, ManifestName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject manifest
                || manifest["schemaVersion"]?.GetValue<int>() is not CurrentSchemaVersion
                || manifest["projectFilePath"]?.GetValue<string>() is not { } project
                || !string.Equals(Path.GetFullPath(project), ProjectFilePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || !DateTime.TryParse(manifest["writtenAt"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime writtenAt)
                || manifest["files"] is not JsonArray list)
            {
                return null;
            }

            List<string> files = [];
            foreach (string relative in list.Select(item => item?.GetValue<string>()).OfType<string>())
            {
                try
                {
                    files.Add(Relative(Path.Combine(ProjectDirectory, relative)));
                }
                catch (ArgumentException)
                {
                    // A path that leaves the project folder is never restored.
                }
            }

            return (writtenAt.ToUniversalTime(), files);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    // The path relative to the project folder; paths outside it are refused.
    private string Relative(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.TrimEndingDirectorySeparator(ProjectDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{path}' is outside the project folder.", nameof(path));
        }

        return fullPath[root.Length..];
    }
}
