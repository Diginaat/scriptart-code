using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using ScriptArt.ProjectSystem;

namespace ScriptArt.UiTests;

/// <summary>The Explorer, editor tabs, and preferences, driven through the window as a user would (D-043, D-044).</summary>
public sealed class ExplorerAndPreferencesTests
{
    [AvaloniaFact]
    public void OpeningAProjectListsItsFilesAndRemembersIt()
    {
        using var session = new IdeSession();
        string folder = session.OpenSample("HelloWorld");

        string[] top = [.. session.Window.FileTree.Items.OfType<TreeViewItem>().Select(item => ((ProjectFileEntry)item.Tag!).Name)];
        Assert.Equal(["Generated", "Graphs", "HelloWorld.csproj", "HelloWorld.vside"], top);
        Assert.Equal([Path.Combine(folder, "HelloWorld.vside")], PreferencesStore.Load(session.PreferencesPath).Preferences.RecentProjects);
        Assert.True(session.Window.RecentMenuItem.IsEnabled);
        Assert.Single(session.Window.DocumentTabs.Children);
    }

    [AvaloniaFact]
    public void CtrlPOpensAFileInATabAndCtrlSSavesIt()
    {
        using var session = new IdeSession();
        string folder = session.OpenSample("HelloWorld");

        session.Press(Key.P, RawInputModifiers.Control);
        session.Type("csproj");
        session.Press(Key.Enter);

        Assert.True(session.Window.FileView.IsVisible);
        Assert.False(session.Window.GraphView.IsVisible);
        Assert.Equal(2, session.Window.DocumentTabs.Children.Count);
        Assert.False(session.Window.FileEditor.IsReadOnly);

        session.Window.FileEditor.Document.Insert(0, "<!-- edited -->\n");
        IdeSession.Settle();
        Assert.Contains("•", session.Window.FileBanner.Text, StringComparison.Ordinal);
        session.Press(Key.S, RawInputModifiers.Control);

        Assert.StartsWith("<!-- edited -->", File.ReadAllText(Path.Combine(folder, "HelloWorld.csproj")), StringComparison.Ordinal);
        Assert.DoesNotContain("•", session.Window.FileBanner.Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void GeneratedFilesOpenReadOnly()
    {
        using var session = new IdeSession();
        session.OpenSample("HelloWorld");

        session.Press(Key.P, RawInputModifiers.Control);
        session.Type("Program.g");
        session.Press(Key.Enter);

        Assert.True(session.Window.FileEditor.IsReadOnly);
        Assert.True(session.Window.FileOpenGraphButton.IsVisible);
    }

    [AvaloniaFact]
    public void NewFileButtonCreatesAndOpensTheFile()
    {
        using var session = new IdeSession();
        string folder = session.OpenSample("HelloWorld");
        Assert.Same(session.Window.MyProjectTab, session.Window.LeftTabs.SelectedItem);
        Assert.False(session.Window.ContinueButton.IsVisible);
        IdeSession.Click(session.Window.NewOtherFileMenuItem);
        IdeSession.InPopup<TextBox>().Single().Text = "notes.md";
        IdeSession.Click(IdeSession.InPopup<Button>().Single(button => button.Content as string == "Create File"));

        Assert.True(File.Exists(Path.Combine(folder, "notes.md")));
        Assert.True(session.Window.FileView.IsVisible);
        Assert.Empty(IdeSession.OpenPopups());
        Assert.Contains(session.Window.FileTree.Items.OfType<TreeViewItem>(), item => ((ProjectFileEntry)item.Tag!).Name == "notes.md");
    }

    [AvaloniaFact]
    public void DeletingAnOpenFileClosesItsTab()
    {
        using var session = new IdeSession();
        string folder = session.OpenSample("HelloWorld");
        IdeSession.Click(session.Window.ExplorerMenuItem);
        IdeSession.Settle();
        string notes = Path.Combine(folder, "notes.md");
        File.WriteAllText(notes, "hello");
        session.Window.FileTree.Items.Clear();
        IdeSession.Click(session.Window.RefreshExplorerButton);
        TreeViewItem item = session.Window.FileTree.Items.OfType<TreeViewItem>().Single(candidate => ((ProjectFileEntry)candidate.Tag!).Name == "notes.md");
        session.Window.FileTree.SelectedItem = item;
        item.Focus();
        session.Press(Key.Enter);
        Assert.Equal(2, session.Window.DocumentTabs.Children.Count);

        item.Focus();
        session.Press(Key.Delete);
        IdeSession.Click(IdeSession.InPopup<Button>().Single(button => button.Content as string == "Delete"));

        Assert.False(File.Exists(notes));
        Assert.Single(session.Window.DocumentTabs.Children);
        Assert.True(session.Window.GraphView.IsVisible);
    }

    [AvaloniaFact]
    public void ManagedFilesCannotBeDeleted()
    {
        using var session = new IdeSession();
        string folder = session.OpenSample("HelloWorld");
        IdeSession.Click(session.Window.ExplorerMenuItem);
        IdeSession.Settle();
        TreeViewItem item = session.Window.FileTree.Items.OfType<TreeViewItem>().Single(candidate => ((ProjectFileEntry)candidate.Tag!).Name == "HelloWorld.vside");
        session.Window.FileTree.SelectedItem = item;
        item.Focus();

        session.Press(Key.Delete);

        Assert.Empty(IdeSession.OpenPopups());
        Assert.True(File.Exists(Path.Combine(folder, "HelloWorld.vside")));
        Assert.Contains("managed by the IDE", session.Window.StatusText.Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void PreferencesAreValidatedSavedAndApplied()
    {
        using var session = new IdeSession();

        session.Press(Key.OemComma, RawInputModifiers.Control);
        Assert.True(session.Window.PreferencesPanel.IsVisible);
        session.Window.AutosaveBox.Value = 5;
        IdeSession.Click(session.Window.PreferencesPanel.GetLogicalDescendants().OfType<Button>().Single(button => button.Content as string == "Save"));
        Assert.True(session.Window.PreferencesPanel.IsVisible);
        Assert.Contains("Autosave must be", session.Window.PreferencesMessage.Text, StringComparison.Ordinal);

        session.Window.AutosaveBox.Value = 30;
        session.Window.UiScaleBox.SelectedIndex = 4;
        session.Window.PreviewBox.IsChecked = false;
        IdeSession.Click(session.Window.PreferencesPanel.GetLogicalDescendants().OfType<Button>().Single(button => button.Content as string == "Save"));

        Preferences saved = PreferencesStore.Load(session.PreferencesPath).Preferences;
        Assert.False(session.Window.PreferencesPanel.IsVisible);
        Assert.Equal(30, saved.AutosaveIntervalSeconds);
        Assert.Equal(1.25, saved.UiScale);
        Assert.False(saved.PreviewBeforeGenerate);
        Assert.False(session.Window.PreviewMenuItem.IsChecked);
        Assert.Equal(1.25, Assert.IsType<ScaleTransform>(session.Window.ScaleRoot.LayoutTransform).ScaleX);
    }

    [AvaloniaFact]
    public void UiScaleAppliesToTheWindowAndItsPopups()
    {
        using var session = new IdeSession("""{ "schemaVersion": 1, "uiScale": 1.5 }""");
        session.OpenSample("HelloWorld");

        session.Press(Key.P, RawInputModifiers.Control);

        Assert.Equal(1.5, Assert.IsType<ScaleTransform>(session.Window.ScaleRoot.LayoutTransform).ScaleX);
        Popup popup = Assert.Single(IdeSession.OpenPopups());
        Assert.True(popup.InheritsTransform);
        Assert.Equal(1.5, popup.Child!.TransformToVisual(session.Window)!.Value.M11, 3);
    }

    [AvaloniaFact]
    public void InvalidPreferencesAreReportedAndTheIdeStarts()
    {
        using var session = new IdeSession("""{ "schemaVersion": 1, "uiScale": 40 }""");

        Assert.Null(session.Window.ScaleRoot.LayoutTransform);
        Assert.Contains("'uiScale'", session.Window.OutputEditor.Text, StringComparison.Ordinal);
    }
}
