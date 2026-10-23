using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ScriptArt.Compiler.Nodes;
using ScriptArt.GraphModel;
using ScriptArt.ProjectSystem;

namespace ScriptArt.App;

/// <summary>
/// Tools → Node Libraries: the node libraries the user approved (D-038). Approved libraries whose files are unchanged are loaded
/// once and added to every project opened afterwards; adding one asks for explicit trust and loads it into the open project at once.
/// </summary>
public partial class MainWindow
{
    private NodeLibraryTrust? trust;

    // The approved libraries loaded so far, added to the node library of every workspace.
    private readonly List<NodeLibraryContents> nodeLibraries = [];

    // The library waiting for the user to trust it.
    private string? pendingLibrary;

    /// <summary>Loads the approved libraries the first time a project opens, reporting problems in Output.</summary>
    private List<NodeLibraryContents> ApprovedNodeLibraries()
    {
        if (trust is not null)
        {
            return nodeLibraries;
        }

        try
        {
            trust = NodeLibraryTrust.Load(NodeLibraryTrust.DefaultSettingsPath);
        }
        catch (Exception exception) when (exception is GraphFormatException or IOException or UnauthorizedAccessException)
        {
            ReportError("Could not read the approved node libraries", exception);
            return nodeLibraries;
        }

        foreach (NodeLibraryContents contents in trust.LoadApproved())
        {
            nodeLibraries.Add(contents);
            foreach (string problem in contents.Problems)
            {
                AppendOutput(problem);
            }
        }

        return nodeLibraries;
    }

    private void OnShowNodeLibraries(object? sender, RoutedEventArgs e)
    {
        ApprovedNodeLibraries();
        RefreshNodeLibraryList();
        TrustPrompt.IsVisible = false;
        NodeLibrariesPanel.IsVisible = true;
    }

    private void OnCloseNodeLibraries(object? sender, RoutedEventArgs e)
    {
        NodeLibrariesPanel.IsVisible = false;
        pendingLibrary = null;
        GraphCanvas.Focus();
    }

    private void RefreshNodeLibraryList()
    {
        NodeLibraryList.Items.Clear();
        foreach (ApprovedNodeLibrary library in trust?.Libraries ?? [])
        {
            NodeLibraryContents? loaded = nodeLibraries.FindLast(contents => string.Equals(contents.AssemblyPath, library.Path, StringComparison.OrdinalIgnoreCase));
            string state = loaded switch
            {
                null => "approved; loads when a project opens",
                { Definitions.Count: > 0 } => $"{loaded.Definitions.Count} node(s)",
                { Problems: [var problem, ..] } => problem,
                _ => "no nodes",
            };
            var item = new ListBoxItem
            {
                Content = new TextBlock { Text = $"{Path.GetFileName(library.Path)} — {state}", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                Tag = library,
            };
            ToolTip.SetTip(item, $"{library.Path}\nSHA-256 {library.Sha256}");
            NodeLibraryList.Items.Add(item);
        }

        if (NodeLibraryList.Items.Count == 0)
        {
            NodeLibraryList.Items.Add(new ListBoxItem { Content = new TextBlock { Text = "No node libraries are approved.", Classes = { "secondary" } }, IsEnabled = false });
        }
    }

    private async void OnAddNodeLibrary(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a node library",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Node library") { Patterns = ["*.dll"] }],
        });
        if (files is [var file] && file.TryGetLocalPath() is { } path)
        {
            pendingLibrary = path;
            TrustText.Text = $"Trust {Path.GetFileName(path)}? Its code runs now and every time ScriptArt Code starts, with your permissions.";
            TrustPrompt.IsVisible = true;
        }
    }

    private void OnCancelTrust(object? sender, RoutedEventArgs e)
    {
        pendingLibrary = null;
        TrustPrompt.IsVisible = false;
    }

    private void OnTrustNodeLibrary(object? sender, RoutedEventArgs e)
    {
        TrustPrompt.IsVisible = false;
        if (pendingLibrary is not { } path)
        {
            return;
        }

        ApprovedNodeLibraries();
        if (trust is null)
        {
            return;
        }

        pendingLibrary = null;
        ApprovedNodeLibrary approved;
        try
        {
            approved = trust.Approve(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ReportError("Could not approve the node library", exception);
            return;
        }

        NodeLibraryContents contents = NodeLibraryLoader.Load(approved.Path, approved.Sha256);
        nodeLibraries.Add(contents);
        List<string> problems = [.. contents.Problems];
        if (workspace is not null)
        {
            problems.AddRange(workspace.Compiler.Library.RegisterLibrary(contents));
            paletteVariables = "";
            RefreshPaletteForVariables();
            Recompile();
        }

        foreach (string problem in problems)
        {
            AppendOutput(problem);
        }

        RefreshNodeLibraryList();
        SetStatus(contents.Definitions.Count > 0
            ? $"Loaded {contents.Definitions.Count} node(s) from {Path.GetFileName(path)}."
            : $"{Path.GetFileName(path)} added no nodes. See Output.");
    }

    private void OnRemoveNodeLibrary(object? sender, RoutedEventArgs e)
    {
        if (trust is null || NodeLibraryList.SelectedItem is not ListBoxItem { Tag: ApprovedNodeLibrary library })
        {
            SetStatus("Select a library to remove.");
            return;
        }

        try
        {
            trust.Remove(library.Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReportError("Could not remove the node library", exception);
            return;
        }

        RefreshNodeLibraryList();
        SetStatus($"Removed {Path.GetFileName(library.Path)}. Its nodes stay available until ScriptArt Code restarts.");
    }
}
