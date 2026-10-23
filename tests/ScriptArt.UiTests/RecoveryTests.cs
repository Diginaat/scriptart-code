using Avalonia.Headless.XUnit;
using Avalonia.Input;

namespace ScriptArt.UiTests;

/// <summary>Autosave and recovery through the window: unsaved work survives a session that ends without saving (D-045).</summary>
public sealed class RecoveryTests
{
    [AvaloniaFact]
    public void UnsavedWorkIsOfferedAfterACrashAndRestored()
    {
        var crashed = new IdeSession();
        string folder = crashed.OpenSample("HelloWorld");
        string csproj = Path.Combine(folder, "HelloWorld.csproj");
        string saved = File.ReadAllText(csproj);
        OpenFile(crashed, "csproj");
        crashed.Window.FileEditor.Document.Insert(0, "<!-- unsaved -->\n");
        crashed.Window.Autosave();
        crashed.Abandon();
        Assert.Equal(saved, File.ReadAllText(csproj));

        using var session = new IdeSession(folder: crashed.Folder);
        session.OpenSample("HelloWorld");

        Assert.True(session.Window.DiffPreview.IsVisible);
        Assert.Equal("Restore", session.Window.WriteDiffButton.Content);
        IdeSession.Click(session.Window.WriteDiffButton);

        Assert.False(session.Window.DiffPreview.IsVisible);
        Assert.StartsWith("<!-- unsaved -->", File.ReadAllText(csproj), StringComparison.Ordinal);
        Assert.Equal("Write Files", session.Window.WriteDiffButton.Content);
        Assert.False(Directory.Exists(Path.Combine(session.Folder, "settings", "recovery")) && Directory.EnumerateFileSystemEntries(Path.Combine(session.Folder, "settings", "recovery")).Any());
    }

    [AvaloniaFact]
    public void DiscardedRecoveryLeavesTheFilesAlone()
    {
        var crashed = new IdeSession();
        string folder = crashed.OpenSample("HelloWorld");
        string csproj = Path.Combine(folder, "HelloWorld.csproj");
        string saved = File.ReadAllText(csproj);
        OpenFile(crashed, "csproj");
        crashed.Window.FileEditor.Document.Insert(0, "<!-- unsaved -->\n");
        crashed.Window.Autosave();
        crashed.Abandon();

        using var session = new IdeSession(folder: crashed.Folder);
        session.OpenSample("HelloWorld");
        IdeSession.Click(session.Window.CancelDiffButton);

        Assert.Equal(saved, File.ReadAllText(csproj));
        session.Window.OpenProjectFile(Path.Combine(folder, "HelloWorld.vside"));
        IdeSession.Settle();
        Assert.False(session.Window.DiffPreview.IsVisible);
    }

    [AvaloniaFact]
    public void SavingRemovesTheRecoveryCopies()
    {
        using var session = new IdeSession();
        string folder = session.OpenSample("HelloWorld");
        OpenFile(session, "csproj");
        session.Window.FileEditor.Document.Insert(0, "<!-- edited -->\n");
        session.Window.Autosave();
        string recovery = Path.Combine(session.Folder, "settings", "recovery");
        Assert.NotEmpty(Directory.EnumerateFiles(recovery, "*", SearchOption.AllDirectories));

        session.Press(Key.S, RawInputModifiers.Control);

        Assert.Empty(Directory.EnumerateFiles(recovery, "*", SearchOption.AllDirectories));
        Assert.StartsWith("<!-- edited -->", File.ReadAllText(Path.Combine(folder, "HelloWorld.csproj")), StringComparison.Ordinal);
    }

    private static void OpenFile(IdeSession session, string name)
    {
        session.Press(Key.P, RawInputModifiers.Control);
        session.Type(name);
        session.Press(Key.Enter);
    }
}
