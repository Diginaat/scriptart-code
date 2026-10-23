using ScriptArt.Compiler.Emit;
using ScriptArt.ProjectSystem;

namespace ScriptArt.Tests;

/// <summary><c>preferences.json</c>: defaults, the version-1 format, invalid values, and settings from newer builds (D-044).</summary>
public sealed class PreferencesTests
{
    [Fact]
    public void MissingFileGivesDefaults()
    {
        using var directory = new TemporaryDirectory();
        PreferencesLoadResult loaded = PreferencesStore.Load(Path.Combine(directory.Path, "preferences.json"));

        Assert.Equal(new Preferences(), loaded.Preferences);
        Assert.Empty(loaded.Problems);
    }

    [Fact]
    public void Version1FileLoads()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "preferences.json");
        string project = Path.Combine(directory.Path, "Hello", "Hello.vside");
        string debugger = Path.Combine(directory.Path, "netcoredbg");
        File.WriteAllText(path, $$"""
            {
              "schemaVersion": 1,
              "recentProjects": [ {{System.Text.Json.JsonSerializer.Serialize(project)}} ],
              "autosaveIntervalSeconds": 120,
              "previewBeforeGenerate": false,
              "debuggerPath": {{System.Text.Json.JsonSerializer.Serialize(debugger)}},
              "theme": "dark",
              "uiScale": 1.25
            }
            """);

        PreferencesLoadResult loaded = PreferencesStore.Load(path);

        Assert.Empty(loaded.Problems);
        Assert.Equal([project], loaded.Preferences.RecentProjects);
        Assert.Equal(120, loaded.Preferences.AutosaveIntervalSeconds);
        Assert.False(loaded.Preferences.PreviewBeforeGenerate);
        Assert.Equal(debugger, loaded.Preferences.DebuggerPath);
        Assert.Equal(IdeTheme.Dark, loaded.Preferences.Theme);
        Assert.Equal(1.25, loaded.Preferences.UiScale);
    }

    [Fact]
    public void SavedPreferencesRoundTrip()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "settings", "preferences.json");
        Preferences preferences = new Preferences
            {
                AutosaveIntervalSeconds = 0, UiScale = 1.5, PreviewBeforeGenerate = false, GodotPath = Path.Combine(directory.Path, "godot.exe"),
            }
            .WithRecentProject(Path.Combine(directory.Path, "A.vside"))
            .WithRecentProject(Path.Combine(directory.Path, "B.vside"))
            .WithRecentProject(Path.Combine(directory.Path, "A.vside"));

        PreferencesStore.Save(path, preferences);
        PreferencesLoadResult loaded = PreferencesStore.Load(path);

        Assert.Empty(loaded.Problems);
        Assert.Equal(preferences.RecentProjects, loaded.Preferences.RecentProjects);
        Assert.Equal([Path.Combine(directory.Path, "A.vside"), Path.Combine(directory.Path, "B.vside")], loaded.Preferences.RecentProjects);
        Assert.Equal(preferences with { RecentProjects = loaded.Preferences.RecentProjects, ApprovedSourceGenerators = loaded.Preferences.ApprovedSourceGenerators }, loaded.Preferences);
        Assert.Contains("\"schemaVersion\": 1", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void ApprovedSourceGeneratorsRoundTripAndReplaceEarlierApprovals()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "preferences.json");
        string generator = Path.Combine(directory.Path, "Gen.dll");
        Preferences preferences = new Preferences()
            .WithApprovedSourceGenerators([new(generator, new string('a', 64))])
            .WithApprovedSourceGenerators([new(generator, new string('b', 64))]);

        PreferencesStore.Save(path, preferences);
        PreferencesLoadResult loaded = PreferencesStore.Load(path);

        Assert.Empty(loaded.Problems);
        Assert.Equal([new SourceGeneratorAssembly(generator, new string('b', 64))], loaded.Preferences.ApprovedSourceGenerators);
    }

    [Fact]
    public void RecentProjectsAreCapped()
    {
        var preferences = new Preferences();
        for (int i = 0; i < Preferences.MaxRecentProjects + 5; i++)
        {
            preferences = preferences.WithRecentProject(Path.Combine(Path.GetTempPath(), $"P{i}.vside"));
        }

        Assert.Equal(Preferences.MaxRecentProjects, preferences.RecentProjects.Count);
        Assert.EndsWith("P14.vside", preferences.RecentProjects[0], StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidValuesFallBackToDefaultsAndAreReported()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "preferences.json");
        File.WriteAllText(path, """
            {
              "schemaVersion": 1,
              "recentProjects": "not a list",
              "autosaveIntervalSeconds": 3,
              "previewBeforeGenerate": "yes",
              "debuggerPath": "relative/netcoredbg",
              "godotPath": "relative/godot",
              "approvedSourceGenerators": [ { "path": "relative/Gen.dll", "sha256": "abc" } ],
              "theme": "neon",
              "uiScale": 9
            }
            """);

        PreferencesLoadResult loaded = PreferencesStore.Load(path);

        Assert.Equal(new Preferences(), loaded.Preferences);
        Assert.Equal(8, loaded.Problems.Count);
        Assert.Contains(loaded.Problems, problem => problem.Contains("'theme'", StringComparison.Ordinal));
    }

    [Fact]
    public void UnreadableFileIsKeptAndDefaultsAreUsed()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "preferences.json");
        File.WriteAllText(path, "{ not json");

        PreferencesLoadResult loaded = PreferencesStore.Load(path);
        PreferencesStore.Save(path, loaded.Preferences);

        Assert.Equal(new Preferences(), loaded.Preferences);
        Assert.Contains(loaded.Problems, problem => problem.Contains("not valid JSON", StringComparison.Ordinal));
        Assert.Equal("{ not json", File.ReadAllText(Path.Combine(directory.Path, "preferences.invalid.json")));
        Assert.Empty(PreferencesStore.Load(path).Problems);
    }

    [Fact]
    public void SettingsFromANewerBuildAreKept()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "preferences.json");
        File.WriteAllText(path, """{ "schemaVersion": 7, "uiScale": 1.1, "futureSetting": { "on": true } }""");

        PreferencesLoadResult loaded = PreferencesStore.Load(path);
        PreferencesStore.Save(path, loaded.Preferences with { UiScale = 2.0 });
        string saved = File.ReadAllText(path);

        Assert.Equal(1.1, loaded.Preferences.UiScale);
        Assert.Contains(loaded.Problems, problem => problem.Contains("newer version", StringComparison.Ordinal));
        Assert.Contains("\"futureSetting\"", saved, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": 7", saved, StringComparison.Ordinal);
        Assert.Equal(2.0, PreferencesStore.Load(path).Preferences.UiScale);
    }
}
