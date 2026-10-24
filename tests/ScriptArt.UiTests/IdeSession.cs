using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using ScriptArt.App;

[assembly: AvaloniaTestApplication(typeof(ScriptArt.UiTests.TestApplication))]

namespace ScriptArt.UiTests;

/// <summary>The IDE's application on Avalonia's headless platform.</summary>
public static class TestApplication
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App.App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont();
}

/// <summary>
/// A main window with its settings in a temporary folder (never the user's), and a copy of a sample project to work on. Disposing
/// closes the window and deletes the folder.
/// </summary>
internal sealed class IdeSession : IDisposable
{
    private static readonly string SamplesFolder = FindSamples();

    // Flyouts and menus are not in the window's logical tree, so popups are tracked as they open and close.
    private static readonly List<Popup> Popups = [];

    private bool abandoned;

    static IdeSession() => Popup.IsOpenProperty.Changed.AddClassHandler<Popup>((popup, _) =>
    {
        Popups.Remove(popup);
        if (popup.IsOpen)
        {
            Popups.Add(popup);
        }
    });

    public IdeSession(string? preferencesJson = null, string? folder = null)
    {
        Folder = folder ?? Path.Combine(Path.GetTempPath(), "ScriptArt.UiTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        PreferencesPath = Path.Combine(Folder, "settings", "preferences.json");
        if (preferencesJson is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencesPath)!);
            File.WriteAllText(PreferencesPath, preferencesJson);
        }

        MainWindow.PreferencesPath = PreferencesPath;
        MainWindow.NodeLibrariesPath = Path.Combine(Folder, "settings", "node-libraries.json");
        MainWindow.RecoveryRoot = Path.Combine(Folder, "settings", "recovery");
        Window = new MainWindow();
        Window.Show();
        Settle();
    }

    public string Folder { get; }

    public string PreferencesPath { get; }

    public MainWindow Window { get; }

    /// <summary>Copies a sample into the session folder (unless it is there already), opens it, and returns its project folder.</summary>
    public string OpenSample(string name)
    {
        string target = Path.Combine(Folder, name);
        if (!Directory.Exists(target))
        {
            CopyDirectory(Path.Combine(SamplesFolder, name), target);
        }

        Window.OpenProjectFile(Path.Combine(target, name + ".vside"));
        Settle();
        return target;
    }

    /// <summary>Runs queued UI work, including layout and posted actions.</summary>
    public static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    public void Press(Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Window.KeyPressQwerty(PhysicalKeyFor(key), modifiers);
        Window.KeyReleaseQwerty(PhysicalKeyFor(key), modifiers);
        Settle();
    }

    public void Type(string text)
    {
        Window.KeyTextInput(text);
        Settle();
    }

    public static void Click(Control control)
    {
        control.RaiseEvent(new RoutedEventArgs(control is MenuItem ? MenuItem.ClickEvent : Button.ClickEvent));
        Settle();
    }

    /// <summary>The open popups (flyouts and menus) of the window, newest last.</summary>
    public static IReadOnlyList<Popup> OpenPopups() => [.. Popups.Where(popup => popup.IsOpen && popup.Child is not ToolTip)];

    /// <summary>The controls of type <typeparamref name="T"/> in the newest open popup.</summary>
    public static IReadOnlyList<T> InPopup<T>()
        where T : Control
    {
        Popup popup = Assert.Single(OpenPopups().TakeLast(1));
        return [.. ((ILogical)popup.Child!).GetSelfAndLogicalDescendants().OfType<T>()];
    }

    /// <summary>Leaves the window as a crash would: nothing is saved or cleaned up, and the folder stays for the next session.</summary>
    public void Abandon()
    {
        abandoned = true;
        Window.Hide();
        Settle();
    }

    public void Dispose()
    {
        if (abandoned)
        {
            return;
        }

        Window.Close();
        Settle();
        MainWindow.PreferencesPath = ProjectSystem.PreferencesStore.DefaultPath;
        MainWindow.NodeLibrariesPath = ProjectSystem.NodeLibraryTrust.DefaultSettingsPath;
        MainWindow.RecoveryRoot = ProjectSystem.ProjectRecovery.DefaultRoot;
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: the file watcher may still hold a handle for a moment.
        }
    }

    private static PhysicalKey PhysicalKeyFor(Key key) => key switch
    {
        Key.Enter => PhysicalKey.Enter,
        Key.Escape => PhysicalKey.Escape,
        Key.Down => PhysicalKey.ArrowDown,
        Key.Delete => PhysicalKey.Delete,
        Key.F2 => PhysicalKey.F2,
        Key.S => PhysicalKey.S,
        Key.P => PhysicalKey.P,
        Key.W => PhysicalKey.W,
        Key.OemComma => PhysicalKey.Comma,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Add the key to the map."),
    };

    private static string FindSamples()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string samples = Path.Combine(directory.FullName, "samples");
            if (Directory.Exists(Path.Combine(samples, "HelloWorld")))
            {
                return samples;
            }
        }

        throw new DirectoryNotFoundException("The samples folder was not found above the test output.");
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (string directory in Directory.EnumerateDirectories(source).Where(path => Path.GetFileName(path) is not ("bin" or "obj")))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }
}
