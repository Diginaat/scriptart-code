using System.Text.Json;
using System.Text.Json.Serialization;
using ScriptArt.Compiler.Nodes;
using ScriptArt.GraphModel;

namespace ScriptArt.ProjectSystem;

/// <summary>A node library the user approved, with the hash of the file they approved.</summary>
public sealed record ApprovedNodeLibrary(string Path, string Sha256);

/// <summary>
/// The node libraries the user approved, kept per user (not per project), with the hash of each approved file. Only approved files
/// whose content is unchanged are loaded; opening a project never approves or loads a library (D-038).
/// </summary>
public sealed class NodeLibraryTrust
{
    private readonly List<ApprovedNodeLibrary> libraries;

    private NodeLibraryTrust(string settingsPath, List<ApprovedNodeLibrary> libraries)
    {
        SettingsPath = settingsPath;
        this.libraries = libraries;
    }

    /// <summary>Where the approved libraries are stored for this user: <c>node-libraries.json</c> in the application data folder.</summary>
    public static string DefaultSettingsPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScriptArtCode", "node-libraries.json");

    public string SettingsPath { get; }

    public IReadOnlyList<ApprovedNodeLibrary> Libraries => libraries;

    /// <summary>Reads the approved libraries; a missing file means none.</summary>
    public static NodeLibraryTrust Load(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        if (!File.Exists(settingsPath))
        {
            return new(settingsPath, []);
        }

        try
        {
            TrustFile? file = JsonSerializer.Deserialize(File.ReadAllText(settingsPath), TrustJsonContext.Default.TrustFile);
            return new(settingsPath, [.. (file?.Libraries ?? []).Where(library => !string.IsNullOrWhiteSpace(library.Path) && !string.IsNullOrWhiteSpace(library.Sha256))]);
        }
        catch (JsonException exception)
        {
            throw new GraphFormatException($"{settingsPath}: the list of approved node libraries is not valid: {exception.Message}", exception);
        }
    }

    /// <summary>Approves the current content of <paramref name="assemblyPath"/>, replacing an earlier approval of the same path.</summary>
    public ApprovedNodeLibrary Approve(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        string path = System.IO.Path.GetFullPath(assemblyPath);
        if (!string.Equals(System.IO.Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A node library is a .dll file.", nameof(assemblyPath));
        }

        var approved = new ApprovedNodeLibrary(path, NodeLibraryLoader.HashFile(path));
        libraries.RemoveAll(library => SamePath(library.Path, path));
        libraries.Add(approved);
        Save();
        return approved;
    }

    /// <summary>Withdraws the approval of <paramref name="assemblyPath"/>. A library already loaded stays loaded until the IDE restarts.</summary>
    public bool Remove(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        string path = System.IO.Path.GetFullPath(assemblyPath);
        bool removed = libraries.RemoveAll(library => SamePath(library.Path, path)) > 0;
        if (removed)
        {
            Save();
        }

        return removed;
    }

    /// <summary>Loads every approved library whose file is unchanged. Changed, missing, and faulty libraries are reported, not loaded.</summary>
    public IReadOnlyList<NodeLibraryContents> LoadApproved() =>
        [.. libraries.Select(library => NodeLibraryLoader.Load(library.Path, library.Sha256))];

    private void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsPath)!);
        AtomicFile.WriteAllText(SettingsPath, JsonSerializer.Serialize(new TrustFile { Libraries = libraries }, TrustJsonContext.Default.TrustFile) + "\n");
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal sealed class TrustFile
    {
        public int SchemaVersion { get; set; } = 1;

        public List<ApprovedNodeLibrary> Libraries { get; set; } = [];
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, NewLine = "\n")]
[JsonSerializable(typeof(NodeLibraryTrust.TrustFile))]
internal sealed partial class TrustJsonContext : JsonSerializerContext;
