using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using ScriptArt.GraphModel;
using ScriptArt.ProjectSystem;

namespace ScriptArt.App;

/// <summary>
/// The tabs above the editor area, one per open graph or file, so it is always clear what is being edited (D-043). Clicking a tab
/// shows it; the × or a middle click closes it, asking first when a file has unsaved changes. Ctrl+Tab cycles, Ctrl+W closes.
/// </summary>
public partial class MainWindow
{
    // A graph by its document, which survives saving and renaming, or a file by its full path.
    private sealed record DocumentTab(GraphDocument? Graph, string? FilePath)
    {
        public bool Matches(DocumentTab other) => Graph is not null
            ? Graph == other.Graph
            : other.FilePath is not null && string.Equals(FilePath, other.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    private readonly List<DocumentTab> documents = [];
    private DocumentTab? activeDocument;

    private void ResetDocuments()
    {
        documents.Clear();
        activeDocument = null;
        shownFile = null;
        FileView.IsVisible = false;
        GraphView.IsVisible = true;
    }

    /// <summary>Shows the canvas with the workspace's open graph and makes its tab the active one.</summary>
    private void ShowGraphDocument()
    {
        if (workspace is null)
        {
            return;
        }

        shownFile = null;
        FileView.IsVisible = false;
        GraphView.IsVisible = true;
        Activate(new DocumentTab(workspace.Graph.Graph, null));
    }

    private void ShowFileDocument(string fullPath) => Activate(new DocumentTab(null, fullPath));

    // Makes a tab active, adding it after the current one when it is new.
    private void Activate(DocumentTab tab)
    {
        DocumentTab? existing = documents.Find(tab.Matches);
        if (existing is null)
        {
            int index = activeDocument is null ? documents.Count : documents.IndexOf(activeDocument) + 1;
            documents.Insert(Math.Clamp(index, 0, documents.Count), tab);
            existing = tab;
        }

        activeDocument = existing;
        RefreshDocumentTabs();
    }

    /// <summary>Shows a tab's graph or file.</summary>
    private void OpenDocument(DocumentTab tab)
    {
        if (workspace is null)
        {
            return;
        }

        if (tab.Graph is not null)
        {
            if (Current(tab.Graph) is not { } graph)
            {
                documents.Remove(tab);
                RefreshDocumentTabs();
                return;
            }

            if (graph != workspace.Graph || shownFile is not null)
            {
                OpenProjectGraph(graph);
            }
        }
        else if (tab.FilePath is { } path)
        {
            ShowCodeFile(path);
        }
    }

    private void CycleDocument(int step)
    {
        if (documents.Count > 1 && activeDocument is not null)
        {
            int index = (documents.IndexOf(activeDocument) + step + documents.Count) % documents.Count;
            OpenDocument(documents[index]);
        }
    }

    private void OnCloseTab(object? sender, RoutedEventArgs e)
    {
        if (activeDocument is not null)
        {
            CloseDocument(activeDocument, anchor: null);
        }
    }

    /// <summary>Closes a tab; a file with unsaved changes asks whether to save them first. The last tab stays open.</summary>
    private void CloseDocument(DocumentTab tab, Control? anchor)
    {
        if (workspace is null)
        {
            return;
        }

        if (documents.Count <= 1)
        {
            SetStatus("The last tab stays open. Open another graph or file first.");
            return;
        }

        if (tab.FilePath is { } path && workspace.IsCodeFileDirty(path))
        {
            AskToSave(tab, anchor ?? (Control)DocumentTabs);
            return;
        }

        RemoveDocument(tab);
    }

    private void RemoveDocument(DocumentTab tab)
    {
        int index = documents.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        documents.RemoveAt(index);
        if (tab.FilePath is { } path && workspace is not null && !workspace.IsCodeFileDirty(path))
        {
            workspace.ForgetFile(path);
        }

        if (activeDocument == tab)
        {
            activeDocument = null;
            if (documents.Count > 0)
            {
                OpenDocument(documents[Math.Min(index, documents.Count - 1)]);
            }
        }

        RefreshDocumentTabs();
        UpdateTitle();
        UpdateCommandStates();
    }

    private void AskToSave(DocumentTab tab, Control anchor)
    {
        string name = Path.GetFileName(tab.FilePath!);
        var save = new Button { Content = "Save", IsDefault = true };
        var discard = new Button { Content = "Don't Save" };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var flyout = new Flyout
        {
            Placement = PlacementMode.Bottom,
            Content = new StackPanel
            {
                Spacing = 10,
                MaxWidth = 340,
                Children =
                {
                    new TextBlock { Text = $"Save the changes to {name}?", FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = "Your changes are lost if you don't save them.", Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { save, discard, cancel } },
                },
            },
        };
        save.Click += (_, _) =>
        {
            flyout.Hide();
            if (workspace is null)
            {
                return;
            }

            try
            {
                workspace.SaveFile(tab.FilePath!);
                RemoveDocument(tab);
                SetStatus($"Saved {name}.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                ReportError($"Could not save {name}", exception);
            }
        };
        discard.Click += (_, _) =>
        {
            flyout.Hide();
            workspace?.RevertFile(tab.FilePath!);
            workspace?.ForgetFile(tab.FilePath!);
            RemoveDocument(tab);
            CheckHandwrittenCode();
        };
        cancel.Click += (_, _) => flyout.Hide();
        flyout.ShowAt(anchor);
    }

    /// <summary>Removes tabs whose graph was removed or whose file is gone without unsaved changes.</summary>
    private void PruneDocuments()
    {
        if (workspace is null)
        {
            return;
        }

        foreach (DocumentTab tab in documents.ToArray())
        {
            bool gone = tab.Graph is not null
                ? Current(tab.Graph) is null
                : !File.Exists(tab.FilePath) && !workspace.IsCodeFileDirty(tab.FilePath!);
            if (gone && documents.Count > 1)
            {
                if (tab.FilePath is { } path)
                {
                    workspace.ForgetFile(path);
                }

                RemoveDocument(tab);
            }
        }

        RefreshDocumentTabs();
    }

    /// <summary>Follows files renamed in the Explorer: their tabs and the shown file move with them.</summary>
    private void RenameDocuments(string oldPath, string newPath)
    {
        string from = Path.GetFullPath(oldPath);
        string to = Path.GetFullPath(newPath);
        string? Renamed(string path) => string.Equals(path, from, StringComparison.OrdinalIgnoreCase) ? to
            : path.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? to + path[from.Length..]
            : null;
        for (int i = 0; i < documents.Count; i++)
        {
            if (documents[i].FilePath is { } path && Renamed(path) is { } renamed)
            {
                bool active = documents[i] == activeDocument;
                documents[i] = new DocumentTab(null, renamed);
                if (active)
                {
                    activeDocument = documents[i];
                }
            }
        }

        if (shownFile is not null && Renamed(shownFile) is { } shown)
        {
            shownFile = shown;
            FileEditor.SyntaxHighlighting = HighlightingFor(shown);
            UpdateFileBanner();
            UpdateFileStatus();
        }

        RefreshDocumentTabs();
        UpdateTitle();
    }

    private void RefreshDocumentTabs()
    {
        DocumentTabs.Children.Clear();
        if (workspace is null)
        {
            return;
        }

        foreach (DocumentTab tab in documents)
        {
            DocumentTabs.Children.Add(TabControlFor(tab));
        }
    }

    private Border TabControlFor(DocumentTab tab)
    {
        (string icon, IBrush iconBrush, string title, string tip) = Describe(tab);
        bool dirty = tab.FilePath is { } path ? workspace!.IsCodeFileDirty(path) : workspace!.EditorIsDirty(tab.Graph!);
        var close = new Button
        {
            Classes = { "icon" },
            Content = dirty ? "●" : "×",
            FontSize = dirty ? 10 : 14,
            Margin = new Avalonia.Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(close, dirty ? "Unsaved changes. Close (Ctrl+W)" : "Close (Ctrl+W)");
        var content = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(close, Dock.Right);
        content.Children.Add(close);
        content.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = icon, Foreground = iconBrush, FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = title, Classes = { "docTitle" }, VerticalAlignment = VerticalAlignment.Center },
            },
        });
        var border = new Border { Classes = { "docTab" }, Child = content };
        if (tab == activeDocument)
        {
            border.Classes.Add("active");
            border.BorderBrush = Token("IdeAccent");
        }

        ToolTip.SetTip(border, tip);
        close.Click += (_, e) =>
        {
            CloseDocument(tab, border);
            e.Handled = true;
        };
        border.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Middle)
            {
                CloseDocument(tab, border);
            }
            else if (e.InitialPressMouseButton == MouseButton.Left && tab != activeDocument)
            {
                OpenDocument(tab);
            }
        };
        border.ContextMenu = new ContextMenu
        {
            ItemsSource = new[]
            {
                Item("Close", () => CloseDocument(tab, border), "Ctrl+W"),
                Item("Close Others", () =>
                {
                    OpenDocument(tab);
                    foreach (DocumentTab other in documents.Where(other => other != tab && (other.FilePath is null || !workspace!.IsCodeFileDirty(other.FilePath))).ToArray())
                    {
                        RemoveDocument(other);
                    }
                }),
                Item("Reveal in Explorer", () =>
                {
                    string? target = tab.FilePath ?? (Current(tab.Graph!) is { } graph ? Path.Combine(workspace!.Project.DirectoryPath, graph.RelativePath) : null);
                    if (target is not null)
                    {
                        RevealInExplorer(target, focus: true);
                    }
                }),
            },
        };
        return border;
    }

    // The icon, its color, the title, and the tooltip of a tab.
    private (string Icon, IBrush Brush, string Title, string Tip) Describe(DocumentTab tab)
    {
        if (tab.Graph is { } graph)
        {
            string path = Current(graph)?.RelativePath ?? "";
            return graph.Kind switch
            {
                GraphKind.EntryPoint => ("▶", IdeColors.IconGreen, graph.Name, $"{path}\nThe program's entry graph"),
                GraphKind.Class => ("◆", IdeColors.IconGold, graph.Name, $"{path}\nClass"),
                GraphKind.Struct => ("◇", IdeColors.IconGold, graph.Name, $"{path}\nStruct"),
                GraphKind.Constructor => ("ƒ", IdeColors.IconOrchid, $"{OwnerName(graph)}.new", $"{path}\nConstructor"),
                _ => ("ƒ", IdeColors.IconOrchid, graph.Owner is null ? graph.Name : $"{OwnerName(graph)}.{graph.Name}", $"{path}\n{(graph.Owner is null ? "Function" : "Method")}"),
            };
        }

        string file = tab.FilePath!;
        string relative = Path.GetRelativePath(workspace!.Project.DirectoryPath, file);
        (string icon, IBrush brush) = Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".cs" => ("C#", IdeColors.IconPurple),
            ".csproj" or ".props" or ".targets" => ("⚙", IdeColors.IconBlue),
            ".sln" or ".slnx" => ("▣", IdeColors.IconBlue),
            ".json" or ".vside" => ("{}", IdeColors.IconKhaki),
            ".md" => ("M↓", IdeColors.IconSky),
            _ => ("📄", IdeColors.IconGray),
        };
        string kind = ProjectFiles.KindOf(workspace.Project, file) == ProjectFileKind.Generated ? " (generated, read-only)" : "";
        return (icon, brush, Path.GetFileName(file), relative + kind);
    }

    private string OwnerName(GraphDocument graph) =>
        workspace?.Project.Graphs.FirstOrDefault(candidate => candidate.Graph.Id == graph.Owner)?.Graph.Name ?? "?";
}
