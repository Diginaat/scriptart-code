using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScriptArt.GraphModel;

using ScriptArt.Compiler.Emit;

namespace ScriptArt.ProjectSystem;

/// <summary>The IDE's colors (D-046).</summary>
public enum IdeTheme
{
    Dark,
    Light,

    /// <summary>Follows the operating system's light or dark setting.</summary>
    System,
}

/// <summary>
/// The user's settings, kept per user in <c>preferences.json</c> beside <c>node-libraries.json</c> (D-044). Values are always in
/// range: <see cref="PreferencesStore"/> replaces an invalid stored value with its default and reports it.
/// </summary>
public sealed record Preferences
{
    public const int MaxRecentProjects = 10;
    public const int MinAutosaveSeconds = 10;
    public const int MaxAutosaveSeconds = 3600;
    public const double MinUiScale = 0.75;
    public const double MaxUiScale = 2.0;

    /// <summary>Full paths of recently opened <c>.vside</c> files, most recent first.</summary>
    public IReadOnlyList<string> RecentProjects { get; init; } = [];

    /// <summary>Seconds between autosaves of recovery copies; 0 turns autosave off.</summary>
    public int AutosaveIntervalSeconds { get; init; } = 60;

    /// <summary>Whether F6 shows the changes to generated files before writing them.</summary>
    public bool PreviewBeforeGenerate { get; init; } = true;

    /// <summary>The <c>netcoredbg</c> executable to debug with; null searches for it (<see cref="Debugging.DebuggerLocator"/>).</summary>
    public string? DebuggerPath { get; init; }

    /// <summary>The Godot 4 .NET executable that runs Godot projects (D-047); null until the user chooses one.</summary>
    public string? GodotPath { get; init; }

    /// <summary>
    /// The source generator assemblies the user approved to run while the IDE checks code (D-050), each with the hash of the approved
    /// file. A generator is run only while its file still has that hash.
    /// </summary>
    public IReadOnlyList<SourceGeneratorAssembly> ApprovedSourceGenerators { get; init; } = [];

    public IdeTheme Theme { get; init; } = IdeTheme.Dark;

    /// <summary>The size of the whole UI relative to the platform's scale, from <see cref="MinUiScale"/> to <see cref="MaxUiScale"/>.</summary>
    public double UiScale { get; init; } = 1.0;

    /// <summary>These preferences with <paramref name="projectPath"/> first in <see cref="RecentProjects"/>.</summary>
    public Preferences WithRecentProject(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        string fullPath = Path.GetFullPath(projectPath);
        return this with
        {
            RecentProjects = [.. RecentProjects.Where(path => !PreferencesStore.SamePath(path, fullPath)).Prepend(fullPath).Take(MaxRecentProjects)],
        };
    }

    /// <summary>These preferences with <paramref name="approved"/> approved, replacing earlier approvals of the same files.</summary>
    public Preferences WithApprovedSourceGenerators(IReadOnlyCollection<SourceGeneratorAssembly> approved)
    {
        ArgumentNullException.ThrowIfNull(approved);
        return this with
        {
            ApprovedSourceGenerators = [.. ApprovedSourceGenerators.Where(old => !approved.Any(added => PreferencesStore.SamePath(added.Path, old.Path))), .. approved],
        };
    }

    /// <summary>True for an autosave interval that may be stored: 0 (off) or between the minimum and the maximum.</summary>
    public static bool IsValidAutosaveInterval(int seconds) => seconds == 0 || seconds is >= MinAutosaveSeconds and <= MaxAutosaveSeconds;

    public static bool IsValidUiScale(double scale) => double.IsFinite(scale) && scale is >= MinUiScale and <= MaxUiScale;
}

/// <summary>What <see cref="PreferencesStore.Load"/> read, and the stored values it could not use.</summary>
public sealed record PreferencesLoadResult(Preferences Preferences, IReadOnlyList<string> Problems);

/// <summary>
/// Reads and writes <see cref="Preferences"/>. The file is versioned like the graph format and migrated on load. Reading never
/// fails: a missing file gives the defaults, and an unreadable file or invalid value is reported and replaced by its default.
/// Settings this build does not know, written by a newer build, are kept when saving.
/// </summary>
public static class PreferencesStore
{
    public const int CurrentSchemaVersion = 1;

    // Migrations[n - 1] converts a file of version n to version n + 1.
    private static readonly Func<JsonObject, JsonObject>[] Migrations = [];

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, NewLine = "\n" };

    /// <summary>Where preferences are stored for this user: <c>preferences.json</c> in the application data folder.</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScriptArtCode", "preferences.json");

    public static PreferencesLoadResult Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        List<string> problems = [];
        JsonObject? root = ReadObject(path, problems);
        return new(root is null ? new Preferences() : FromJson(Migrate(root, problems), problems), problems);
    }

    /// <summary>
    /// Writes <paramref name="preferences"/> atomically. Settings already in the file that this build does not know are kept, and a
    /// file from a newer build keeps its version.
    /// </summary>
    public static void Save(string path, Preferences preferences)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(preferences);
        JsonObject root = ReadObject(path, []) is { } existing ? Migrate(existing, []) : [];
        if (SchemaVersionOf(root) <= CurrentSchemaVersion)
        {
            root["schemaVersion"] = CurrentSchemaVersion;
        }

        root["recentProjects"] = new JsonArray([.. preferences.RecentProjects.Select(project => (JsonNode)JsonValue.Create(project))]);
        root["autosaveIntervalSeconds"] = preferences.AutosaveIntervalSeconds;
        root["previewBeforeGenerate"] = preferences.PreviewBeforeGenerate;
        root["debuggerPath"] = preferences.DebuggerPath;
        root["godotPath"] = preferences.GodotPath;
        root["approvedSourceGenerators"] = new JsonArray([.. preferences.ApprovedSourceGenerators.Select(approved =>
            (JsonNode)new JsonObject { ["path"] = approved.Path, ["sha256"] = approved.Sha256 })]);
        root["theme"] = preferences.Theme.ToString().ToLowerInvariant();
        root["uiScale"] = preferences.UiScale;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        AtomicFile.WriteAllText(path, root.ToJsonString(WriteOptions) + "\n");
    }

    internal static bool SamePath(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // The file's top-level object; null when it is missing or not a JSON object, which is reported. An unreadable file is set aside
    // as preferences.invalid.json, so saving the defaults does not lose what the user had.
    private static JsonObject? ReadObject(string path, List<string> problems)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject root)
            {
                return root;
            }

            problems.Add($"{path}: the preferences are not a JSON object, so the defaults are used.");
        }
        catch (JsonException exception)
        {
            problems.Add($"{path}: the preferences are not valid JSON ({exception.Message}), so the defaults are used.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{path}: the preferences could not be read ({exception.Message}), so the defaults are used.");
            return null;
        }

        try
        {
            File.Copy(path, Path.ChangeExtension(path, ".invalid.json"), overwrite: true);
            problems.Add($"The unreadable file was kept as {Path.GetFileName(Path.ChangeExtension(path, ".invalid.json"))}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            problems.Add($"The unreadable file could not be kept: {exception.Message}");
        }

        return null;
    }

    private static int SchemaVersionOf(JsonObject root) =>
        root["schemaVersion"] is JsonValue value && value.TryGetValue(out int version) ? Math.Max(version, 1) : 1;

    private static JsonObject Migrate(JsonObject root, List<string> problems)
    {
        int version = SchemaVersionOf(root);
        if (version > CurrentSchemaVersion)
        {
            problems.Add($"The preferences were written by a newer version of ScriptArt Code (format {version}); settings this version does not know are kept but not used.");
            return root;
        }

        for (; version < CurrentSchemaVersion; version++)
        {
            root = Migrations[version - 1](root);
        }

        return root;
    }

    private static Preferences FromJson(JsonObject root, List<string> problems)
    {
        var defaults = new Preferences();
        T Read<T>(string name, T fallback, Func<JsonNode, (bool Ok, T Value)> parse, string expected)
        {
            if (root[name] is not { } node)
            {
                return fallback;
            }

            try
            {
                (bool ok, T value) = parse(node);
                if (ok)
                {
                    return value;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or FormatException)
            {
                // Reported below, like any other invalid value.
            }

            problems.Add($"Preference '{name}' has the invalid value {node.ToJsonString()}; expected {expected}. The default is used.");
            return fallback;
        }

        IReadOnlyList<string> recent = Read<IReadOnlyList<string>>("recentProjects", defaults.RecentProjects, node =>
        {
            List<string> paths = [.. node.AsArray().Select(item => item?.GetValue<string>()).OfType<string>()
                .Where(path => path.Length > 0 && Path.IsPathFullyQualified(path))];
            return (true, [.. paths.DistinctBy(path => OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path).Take(Preferences.MaxRecentProjects)]);
        }, "a list of project paths");
        int autosave = Read("autosaveIntervalSeconds", defaults.AutosaveIntervalSeconds,
            node => node.GetValue<int>() is var seconds ? (Preferences.IsValidAutosaveInterval(seconds), seconds) : default,
            $"0 (off) or {Preferences.MinAutosaveSeconds} to {Preferences.MaxAutosaveSeconds} seconds");
        bool preview = Read("previewBeforeGenerate", defaults.PreviewBeforeGenerate, node => (true, node.GetValue<bool>()), "true or false");
        string? debugger = Read("debuggerPath", defaults.DebuggerPath, node => node.GetValueKind() == JsonValueKind.Null
            ? (true, null)
            : node.GetValue<string>() is var text ? (text.Length == 0 || Path.IsPathFullyQualified(text), text.Length == 0 ? null : text) : default,
            "a full path or null");
        string? godot = Read("godotPath", defaults.GodotPath, node => node.GetValueKind() == JsonValueKind.Null
            ? (true, null)
            : node.GetValue<string>() is var text ? (text.Length == 0 || Path.IsPathFullyQualified(text), text.Length == 0 ? null : text) : default,
            "a full path or null");
        IReadOnlyList<SourceGeneratorAssembly> generators = Read<IReadOnlyList<SourceGeneratorAssembly>>("approvedSourceGenerators", defaults.ApprovedSourceGenerators, node =>
        {
            SourceGeneratorAssembly?[] read = [.. node.AsArray().Select(item => item?["path"]?.GetValue<string>() is { } file && Path.IsPathFullyQualified(file)
                && item["sha256"]?.GetValue<string>() is { Length: 64 } hash && hash.All(char.IsAsciiHexDigit)
                ? new SourceGeneratorAssembly(file, hash.ToLowerInvariant())
                : null)];
            return (!read.Contains(null), [.. read.OfType<SourceGeneratorAssembly>()]);
        }, "a list of { \"path\": a full path, \"sha256\": a hash }");
        IdeTheme theme = Read("theme", defaults.Theme,
            node => Enum.TryParse(node.GetValue<string>(), ignoreCase: true, out IdeTheme parsed) && Enum.IsDefined(parsed) && !int.TryParse(node.GetValue<string>(), out _)
                ? (true, parsed)
                : (false, defaults.Theme),
            string.Join(" or ", Enum.GetNames<IdeTheme>().Select(name => $"\"{name.ToLowerInvariant()}\"")));
        double scale = Read("uiScale", defaults.UiScale, node => node.GetValue<double>() is var value ? (Preferences.IsValidUiScale(value), value) : default,
            string.Create(CultureInfo.InvariantCulture, $"{Preferences.MinUiScale} to {Preferences.MaxUiScale}"));
        return new Preferences
        {
            RecentProjects = recent,
            AutosaveIntervalSeconds = autosave,
            PreviewBeforeGenerate = preview,
            DebuggerPath = debugger,
            GodotPath = godot,
            ApprovedSourceGenerators = generators,
            Theme = theme,
            UiScale = scale,
        };
    }
}
