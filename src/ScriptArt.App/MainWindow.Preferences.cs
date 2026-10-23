using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using AvaloniaEdit.Highlighting;
using ScriptArt.App.Canvas;
using ScriptArt.ProjectSystem;

namespace ScriptArt.App;

/// <summary>
/// The user's preferences (D-044): read when the IDE starts, edited in Tools → Preferences, and written to <c>preferences.json</c>.
/// Recent projects are offered in File → Open Recent and on the start screen.
/// </summary>
public partial class MainWindow
{
    private static readonly double[] UiScales = [0.75, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0];

    private Preferences preferences = new();

    // Where the per-user settings are read and written; UI tests point these at a temporary folder.
    internal static string PreferencesPath { get; set; } = PreferencesStore.DefaultPath;

    internal static string NodeLibrariesPath { get; set; } = NodeLibraryTrust.DefaultSettingsPath;

    private void InitializePreferences()
    {
        PreferencesLoadResult loaded = PreferencesStore.Load(PreferencesPath);
        preferences = loaded.Preferences;
        foreach (string problem in loaded.Problems)
        {
            AppendOutput(problem);
        }

        ThemeBox.ItemsSource = Enum.GetNames<IdeTheme>();
        UiScaleBox.ItemsSource = UiScales.Select(scale => scale.ToString("P0", CultureInfo.CurrentCulture)).ToArray();
        ActualThemeVariantChanged += (_, _) => OnThemeChanged();
        ApplyPreferences();
        OnThemeChanged();
    }

    private void ApplyPreferences()
    {
        if (Application.Current is { } application)
        {
            application.RequestedThemeVariant = preferences.Theme switch
            {
                IdeTheme.Light => ThemeVariant.Light,
                IdeTheme.System => ThemeVariant.Default,
                _ => ThemeVariant.Dark,
            };
        }

        PreviewMenuItem.IsChecked = preferences.PreviewBeforeGenerate;
        ScaleRoot.LayoutTransform = preferences.UiScale == 1.0 ? null : new ScaleTransform(preferences.UiScale, preferences.UiScale);
        RefreshRecentProjects();
        ScheduleAutosave();
    }

    /// <summary>A brush of the IDE theme (Theme.axaml) for the window's current light or dark variant.</summary>
    private IBrush Token(string key) => (IBrush)this.FindResource(ActualThemeVariant, key)!;

    /// <summary>
    /// Brings the colors drawn in code up to date with the theme: the canvas, syntax and diff colors, and the panels built in code.
    /// Controls declared in XAML follow the theme through dynamic resources.
    /// </summary>
    private void OnThemeChanged()
    {
        bool light = ActualThemeVariant == ThemeVariant.Light;
        CanvasTheme.Use(light);
        GraphCanvas.ThemeChanged();
        CodeEditor.SyntaxHighlighting = Recolored(HighlightingManager.Instance.GetDefinition("C#"));
        if (shownFile is not null)
        {
            FileEditor.SyntaxHighlighting = HighlightingFor(shownFile);
        }

        CodeEditor.TextArea.TextView.Redraw();
        FileEditor.TextArea.TextView.Redraw();
        diffColorizer.UseTheme(light);
        DiffEditor.TextArea.TextView.Redraw();
        RefreshProjectPanel();
        RefreshExplorer();
        RefreshDocumentTabs();
        if (workspace is not null)
        {
            ShowCompilation(workspace.LastCompilation);
        }
    }

    // Writes the preferences; a failure is reported and the IDE keeps the values for this session.
    private void SavePreferences(Preferences changed)
    {
        preferences = changed;
        try
        {
            PreferencesStore.Save(PreferencesPath, changed);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReportError("Could not save the preferences", exception);
        }
    }

    private void RememberRecentProject(string projectFilePath)
    {
        SavePreferences(preferences.WithRecentProject(projectFilePath));
        RefreshRecentProjects();
    }

    private void RefreshRecentProjects()
    {
        List<MenuItem> items = [.. preferences.RecentProjects.Select((path, index) =>
            Item($"_{(index + 1) % 10} {Path.GetFileNameWithoutExtension(path)} — {Path.GetDirectoryName(path)}", () => OpenRecentProject(path)))];
        RecentMenuItem.ItemsSource = items;
        RecentMenuItem.IsEnabled = items.Count > 0;

        RecentProjectsPanel.Children.Clear();
        if (preferences.RecentProjects.Count > 0)
        {
            RecentProjectsPanel.Children.Add(new TextBlock { Text = "Recent", Classes = { "secondary" }, Margin = new Avalonia.Thickness(0, 0, 0, 4) });
        }

        foreach (string path in preferences.RecentProjects)
        {
            var button = new Button
            {
                Classes = { "icon" },
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = Path.GetFileNameWithoutExtension(path), FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = Path.GetDirectoryName(path), Classes = { "secondary" }, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
            };
            ToolTip.SetTip(button, path);
            button.Click += (_, _) => OpenRecentProject(path);
            RecentProjectsPanel.Children.Add(button);
        }
    }

    private void OpenRecentProject(string path)
    {
        if (!File.Exists(path))
        {
            SavePreferences(preferences with { RecentProjects = [.. preferences.RecentProjects.Where(recent => recent != path)] });
            RefreshRecentProjects();
            SetStatus($"{path} no longer exists, so it was removed from the recent projects.");
            return;
        }

        if (workspace?.IsDirty == true)
        {
            SetStatus("Save or undo your changes before opening another project.");
            return;
        }

        OpenProjectFile(path);
    }

    private void OnTogglePreview(object? sender, RoutedEventArgs e) =>
        SavePreferences(preferences with { PreviewBeforeGenerate = PreviewMenuItem.IsChecked });

    private void OnShowPreferences(object? sender, RoutedEventArgs e)
    {
        AutosaveBox.Value = preferences.AutosaveIntervalSeconds;
        PreviewBox.IsChecked = preferences.PreviewBeforeGenerate;
        DebuggerPathBox.Text = preferences.DebuggerPath ?? "";
        GodotPathBox.Text = preferences.GodotPath ?? "";
        ThemeBox.SelectedIndex = (int)preferences.Theme;
        int scale = Array.IndexOf(UiScales, preferences.UiScale);
        UiScaleBox.SelectedIndex = scale >= 0 ? scale : Array.IndexOf(UiScales, 1.0);
        PreferencesMessage.Text = "Autosave keeps recovery copies of unsaved work outside the project and never writes your files. System follows the operating system's light or dark setting.";
        PreferencesPanel.IsVisible = true;
        AutosaveBox.Focus();
    }

    private void OnSavePreferences(object? sender, RoutedEventArgs e)
    {
        int autosave = (int)Math.Round(AutosaveBox.Value ?? 0);
        if (!Preferences.IsValidAutosaveInterval(autosave))
        {
            PreferencesMessage.Text = $"Autosave must be 0 (off) or {Preferences.MinAutosaveSeconds} to {Preferences.MaxAutosaveSeconds} seconds.";
            return;
        }

        string debugger = (DebuggerPathBox.Text ?? "").Trim();
        if (debugger.Length > 0 && (!Path.IsPathFullyQualified(debugger) || !File.Exists(debugger)))
        {
            PreferencesMessage.Text = "netcoredbg must be the full path of an existing file, or empty to search for it.";
            return;
        }

        string godot = (GodotPathBox.Text ?? "").Trim();
        if (godot.Length > 0 && (!Path.IsPathFullyQualified(godot) || !File.Exists(godot)))
        {
            PreferencesMessage.Text = "Godot must be the full path of an existing file, or empty.";
            return;
        }

        SavePreferences(preferences with
        {
            AutosaveIntervalSeconds = autosave,
            PreviewBeforeGenerate = PreviewBox.IsChecked == true,
            DebuggerPath = debugger.Length == 0 ? null : debugger,
            GodotPath = godot.Length == 0 ? null : godot,
            Theme = (IdeTheme)Math.Max(ThemeBox.SelectedIndex, 0),
            UiScale = UiScales[Math.Max(UiScaleBox.SelectedIndex, 0)],
        });
        ApplyPreferences();
        PreferencesPanel.IsVisible = false;
        SetStatus("Preferences saved.");
    }

    private void OnCancelPreferences(object? sender, RoutedEventArgs e) => PreferencesPanel.IsVisible = false;

    private void OnClearRecentProjects(object? sender, RoutedEventArgs e)
    {
        SavePreferences(preferences with { RecentProjects = [] });
        RefreshRecentProjects();
        PreferencesMessage.Text = "Cleared the recent projects.";
    }

    private async void OnBrowseDebugger(object? sender, RoutedEventArgs e)
    {
        if (await ChooseExecutable("Choose the netcoredbg executable") is { } path)
        {
            DebuggerPathBox.Text = path;
        }
    }

    private async void OnBrowseGodot(object? sender, RoutedEventArgs e)
    {
        if (await ChooseExecutable("Choose the Godot 4 .NET executable") is { } path)
        {
            GodotPathBox.Text = path;
        }
    }

    private async Task<string?> ChooseExecutable(string title)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false });
        return files is [var file] ? file.TryGetLocalPath() : null;
    }
}
