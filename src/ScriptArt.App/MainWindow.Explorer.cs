using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ScriptArt.NodeEditor;
using ScriptArt.ProjectSystem;

namespace ScriptArt.App;

/// <summary>
/// The Explorer (D-043): the solution and its projects, and the project folder as a tree. Files open as tabs in the editor area;
/// files and folders are created, renamed, and deleted through <see cref="ProjectFiles"/>, which refuses what the IDE manages.
/// A watcher follows changes made by other tools, such as VS Code, and reloads open files that have no unsaved changes here.
/// </summary>
public partial class MainWindow
{
    // Folders expanded in the tree, by full path, kept when the tree is rebuilt.
    private readonly HashSet<string> expandedFolders = new(StringComparer.OrdinalIgnoreCase);

    // Changes on disk are collected for a moment, then handled together on the UI thread.
    private readonly DispatcherTimer diskChangeTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private FileSystemWatcher? projectWatcher;
    private bool projectFileChanged;

    // The .csproj write time restore was last offered for, so a change is offered once.
    private DateTime restoreOfferedFor;

    private void InitializeExplorer()
    {
        diskChangeTimer.Tick += (_, _) =>
        {
            diskChangeTimer.Stop();
            OnDiskChanged();
        };
        FileTree.KeyDown += (_, e) =>
        {
            if (FileTree.SelectedItem is not TreeViewItem { Tag: ProjectFileEntry entry } item)
            {
                return;
            }

            switch (e.Key)
            {
                case Key.Enter when !entry.IsDirectory:
                    ShowCodeFile(entry.Path);
                    break;
                case Key.Enter:
                    item.IsExpanded = !item.IsExpanded;
                    break;
                case Key.F2:
                    AskToRename(item, entry);
                    break;
                case Key.Delete:
                    ConfirmDelete(item, entry);
                    break;
                default:
                    return;
            }

            e.Handled = true;
        };
    }

    private void OnShowExplorer(object? sender, RoutedEventArgs e)
    {
        if (workspace is not null)
        {
            LeftTabs.SelectedItem = ExplorerTab;
            Dispatcher.UIThread.Post(() => FileTree.Focus(), DispatcherPriority.Background);
        }
    }

    private void OnRefreshExplorer(object? sender, RoutedEventArgs e) => RefreshExplorer();

    private void OnCollapseExplorer(object? sender, RoutedEventArgs e)
    {
        expandedFolders.Clear();
        RefreshExplorer();
    }

    /// <summary>Rebuilds the solution section and the file tree from disk, keeping expanded folders and the selection.</summary>
    private void RefreshExplorer()
    {
        string? selected = (FileTree.SelectedItem as TreeViewItem)?.Tag is ProjectFileEntry entry ? entry.Path : null;
        FileTree.Items.Clear();
        RefreshSolutionPanel();
        if (workspace is null)
        {
            FilesTitle.Text = "FILES";
            return;
        }

        string root = workspace.Project.DirectoryPath;
        FilesTitle.Text = $"FILES — {Path.GetFileName(Path.TrimEndingDirectorySeparator(root)).ToUpperInvariant()}";
        expandedFolders.RemoveWhere(folder => !Directory.Exists(folder));
        foreach (TreeViewItem item in FolderItems(root))
        {
            FileTree.Items.Add(item);
        }

        if (selected is not null)
        {
            SelectInTree(selected);
        }
    }

    private List<TreeViewItem> FolderItems(string directory)
    {
        try
        {
            return [.. ProjectFiles.List(directory).Select(FileItem)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [new TreeViewItem { Header = new TextBlock { Text = $"Could not read the folder: {exception.Message}", Classes = { "secondary" } }, Focusable = false }];
        }
    }

    private TreeViewItem FileItem(ProjectFileEntry entry)
    {
        ProjectFileKind? kind = entry.IsDirectory ? null : ProjectFiles.KindOf(workspace!.Project, entry.Path);
        (string icon, IBrush brush) = entry.IsDirectory ? ("▸", IdeColors.IconGold) : FileIcon(entry.Path, kind!.Value);
        string tip = Path.GetRelativePath(workspace!.Project.DirectoryPath, entry.Path) + kind switch
        {
            ProjectFileKind.Graph => "\nGraph: opens on the canvas",
            ProjectFileKind.Generated => "\nGenerated from a graph (read-only)",
            ProjectFileKind.ProjectFile => "\nThe project file (managed by the IDE)",
            ProjectFileKind.Binary => "\nNot a text file",
            _ => "",
        };
        var name = new TextBlock { Text = entry.Name, VerticalAlignment = VerticalAlignment.Center };
        if (kind is ProjectFileKind.Generated or ProjectFileKind.Binary)
        {
            name.Classes.Add("secondary");
        }

        var item = new TreeViewItem
        {
            Tag = entry,
            Header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = icon, Foreground = brush, FontSize = 11, MinWidth = 16, VerticalAlignment = VerticalAlignment.Center },
                    name,
                },
            },
            [ToolTip.TipProperty] = tip,
        };
        if (entry.IsDirectory)
        {
            if (expandedFolders.Contains(entry.Path))
            {
                item.ItemsSource = FolderItems(entry.Path);
                item.IsExpanded = true;
            }
            else if (HasEntries(entry.Path))
            {
                // A placeholder shows the expander; the folder is read when it is first expanded.
                item.ItemsSource = new[] { new TreeViewItem() };
            }

            item.PropertyChanged += (_, e) =>
            {
                if (e.Property != TreeViewItem.IsExpandedProperty)
                {
                    return;
                }

                if (item.IsExpanded && expandedFolders.Add(entry.Path))
                {
                    item.ItemsSource = FolderItems(entry.Path);
                }
                else if (!item.IsExpanded)
                {
                    expandedFolders.Remove(entry.Path);
                }
            };
        }

        item.DoubleTapped += (_, e) =>
        {
            if (kind == ProjectFileKind.ProjectFile)
            {
                LeftTabs.SelectedItem = MyProjectTab;
                SetStatus("This is the project file, which the IDE manages. Its graphs are listed in My Project; right-click → Open as Text to read it.");
                e.Handled = true;
            }
            else if (!entry.IsDirectory)
            {
                ShowCodeFile(entry.Path);
                e.Handled = true;
            }
        };
        item.ContextMenu = FileMenu(item, entry);
        return item;
    }

    private static bool HasEntries(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory).Any(path => !ProjectFiles.HiddenFolders.Contains(Path.GetFileName(path)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static (string Icon, IBrush Brush) FileIcon(string path, ProjectFileKind kind) => kind switch
    {
        ProjectFileKind.Graph => ("◈", IdeColors.IconGreen),
        ProjectFileKind.ProjectFile => ("◉", IdeColors.IconGreen),
        ProjectFileKind.Generated => ("C#", IdeColors.IconGray),
        ProjectFileKind.CSharp => ("C#", IdeColors.IconPurple),
        ProjectFileKind.Binary => ("■", IdeColors.IconGray),
        _ => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".csproj" or ".props" or ".targets" => ("⚙", IdeColors.IconBlue),
            ".sln" or ".slnx" => ("▣", IdeColors.IconBlue),
            ".json" => ("{}", IdeColors.IconKhaki),
            ".md" => ("M↓", IdeColors.IconSky),
            _ => ("·", IdeColors.IconGray),
        },
    };

    private ContextMenu FileMenu(TreeViewItem item, ProjectFileEntry entry)
    {
        string? protection = ProjectFiles.ProtectionOf(workspace!.Project, entry.Path);
        string folder = entry.IsDirectory ? entry.Path : Path.GetDirectoryName(entry.Path)!;
        List<Control> items = [];
        if (!entry.IsDirectory)
        {
            items.Add(Item(ProjectFiles.KindOf(workspace.Project, entry.Path) == ProjectFileKind.ProjectFile ? "Open as Text" : "Open", () => ShowCodeFile(entry.Path), "Enter"));
        }

        items.Add(Item("Open in VS Code", () => OpenInCode(entry.Path, null)));
        items.Add(new Separator());
        items.Add(Item("New Function Graph", () => OnAddFunction(this, new RoutedEventArgs())));
        items.Add(Item("New C# File…", () => AskForNewItem(item, folder, folder: false, ".cs")));
        items.Add(Item("New File…", () => AskForNewItem(item, folder, folder: false)));
        items.Add(Item("New Folder…", () => AskForNewItem(item, folder, folder: true)));
        items.Add(new Separator());
        MenuItem rename = Item("Rename…", () => AskToRename(item, entry), "F2");
        MenuItem delete = Item("Delete…", () => ConfirmDelete(item, entry), "Delete");
        if (protection is not null)
        {
            rename.IsEnabled = delete.IsEnabled = false;
            ToolTip.SetTip(rename, protection);
            ToolTip.SetTip(delete, protection);
        }

        items.Add(rename);
        items.Add(delete);
        items.Add(new Separator());
        items.Add(Item("Copy Path", () => Clipboard?.SetTextAsync(entry.Path)));
        items.Add(Item("Copy Relative Path", () => Clipboard?.SetTextAsync(Path.GetRelativePath(workspace!.Project.DirectoryPath, entry.Path))));
        return new ContextMenu { ItemsSource = items };
    }

    // The folder new items go into: the selected folder, the selected file's folder, or the project folder.
    private string SelectedFolder() => (FileTree.SelectedItem as TreeViewItem)?.Tag switch
    {
        ProjectFileEntry { IsDirectory: true } folder => folder.Path,
        ProjectFileEntry file => Path.GetDirectoryName(file.Path)!,
        _ => workspace!.Project.DirectoryPath,
    };

    private void OnNewFile(object? sender, RoutedEventArgs e)
    {
        if (workspace is not null && sender is Control anchor)
        {
            AskForNewItem(anchor, SelectedFolder(), folder: false);
        }
    }

    private void OnNewCSharpFile(object? sender, RoutedEventArgs e)
    {
        if (workspace is not null)
        {
            AskForNewItem(NewFileButton, SelectedFolder(), folder: false, ".cs");
        }
    }

    private void OnNewFolder(object? sender, RoutedEventArgs e)
    {
        if (workspace is not null && sender is Control anchor)
        {
            AskForNewItem(anchor, SelectedFolder(), folder: true);
        }
    }

    // A C# file gets .cs added when its name has no extension.
    private void AskForNewItem(Control anchor, string directory, bool folder, string? extension = null)
    {
        string where = Path.GetRelativePath(workspace!.Project.DirectoryPath, directory);
        string title = folder ? $"New folder in {where}" : extension == ".cs" ? $"New C# file in {where}" : $"New file in {where}";
        AskForName(anchor, title, "", folder ? "Create Folder" : "Create File", name =>
        {
            if (extension is not null && !Path.HasExtension(name))
            {
                name += extension;
            }

            string path = folder
                ? ProjectFiles.CreateFolder(workspace!.Project, directory, name)
                : ProjectFiles.CreateFile(workspace!.Project, directory, name);
            expandedFolders.Add(directory);
            RefreshExplorer();
            if (folder)
            {
                SelectInTree(path);
            }
            else
            {
                workspace.RefreshFiles();
                CheckHandwrittenCode();
                ShowCodeFile(path);
            }

            SetStatus($"Created {Path.GetRelativePath(workspace.Project.DirectoryPath, path)}.");
        });
    }

    private void AskToRename(Control anchor, ProjectFileEntry entry)
    {
        if (workspace is null)
        {
            return;
        }

        if (ProjectFiles.ProtectionOf(workspace.Project, entry.Path) is { } reason)
        {
            SetStatus(reason);
            return;
        }

        AskForName(anchor, $"Rename {entry.Name}", entry.Name, "Rename", name =>
        {
            if (name == entry.Name)
            {
                return;
            }

            string renamed = ProjectFiles.Rename(workspace!.Project, entry.Path, name);
            workspace.FileRenamed(entry.Path, renamed);
            RenameDocuments(entry.Path, renamed);
            if (expandedFolders.Remove(entry.Path))
            {
                expandedFolders.Add(renamed);
            }

            workspace.RefreshFiles();
            RefreshExplorer();
            SelectInTree(renamed);
            RefreshProjectPanel();
            CheckHandwrittenCode();
            SetStatus($"Renamed {entry.Name} to {name}.");
        });
    }

    private void ConfirmDelete(Control anchor, ProjectFileEntry entry)
    {
        if (workspace is null)
        {
            return;
        }

        if (ProjectFiles.ProtectionOf(workspace.Project, entry.Path) is { } reason)
        {
            SetStatus(reason);
            return;
        }

        string what = entry.IsDirectory ? $"the folder {entry.Name} and everything in it" : entry.Name;
        var delete = new Button { Content = "Delete" };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var flyout = new Flyout
        {
            Placement = PlacementMode.Right,
            Content = new StackPanel
            {
                Spacing = 8,
                MaxWidth = 320,
                Children =
                {
                    new TextBlock { Text = $"Delete {what}? This cannot be undone.", TextWrapping = TextWrapping.Wrap },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { delete, cancel } },
                },
            },
        };
        delete.Click += (_, _) =>
        {
            flyout.Hide();
            try
            {
                ProjectFiles.Delete(workspace.Project, entry.Path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                ReportError($"Could not delete {entry.Name}", exception);
                return;
            }

            // Open files that were deleted lose their unsaved text too: the user chose to delete them.
            string prefix = Path.TrimEndingDirectorySeparator(entry.Path) + Path.DirectorySeparatorChar;
            foreach (string path in documents.Select(tab => tab.FilePath).OfType<string>()
                .Where(path => string.Equals(path, entry.Path, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                workspace.ForgetFile(path);
            }

            workspace.RefreshFiles();
            PruneDocuments();
            RefreshExplorer();
            RefreshProjectPanel();
            CheckHandwrittenCode();
            SetStatus($"Deleted {entry.Name}.");
        };
        cancel.Click += (_, _) => flyout.Hide();
        flyout.ShowAt(anchor);
    }

    /// <summary>
    /// Asks for a file or folder name in a flyout. <paramref name="apply"/> runs with a valid name; what it throws is shown in the
    /// flyout, which stays open to correct the name.
    /// </summary>
    private static void AskForName(Control anchor, string title, string current, string action, Action<string> apply)
    {
        var box = new TextBox { Text = current, MinWidth = 260 };
        var ok = new Button { Content = action, IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var message = new TextBlock { Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 };
        var flyout = new Flyout
        {
            Placement = PlacementMode.Right,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
                    box,
                    message,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { ok, cancel } },
                },
            },
        };
        void Submit()
        {
            string name = (box.Text ?? "").Trim();
            if (!ProjectFiles.IsValidName(name))
            {
                message.Text = "Enter a name without slashes or the characters : * ? \" < > |.";
                return;
            }

            try
            {
                apply(name);
                flyout.Hide();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                message.Text = exception.Message;
            }
        }

        ok.Click += (_, _) => Submit();
        cancel.Click += (_, _) => flyout.Hide();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Submit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                flyout.Hide();
                e.Handled = true;
            }
        };
        flyout.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            // Select the name without its extension, as renaming usually keeps it.
            int dot = current.LastIndexOf('.');
            box.SelectionStart = 0;
            box.SelectionEnd = dot > 0 ? dot : current.Length;
        });
        flyout.ShowAt(anchor);
    }

    /// <summary>
    /// Expands the folders down to <paramref name="path"/> and selects it in the tree. With <paramref name="focus"/>, the Explorer
    /// is shown and takes the keyboard focus.
    /// </summary>
    private void RevealInExplorer(string path, bool focus)
    {
        if (workspace is null)
        {
            return;
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace.Project.DirectoryPath));
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        bool added = false;
        for (string? folder = Path.GetDirectoryName(fullPath); folder is not null && folder.Length > root.Length; folder = Path.GetDirectoryName(folder))
        {
            added |= expandedFolders.Add(folder);
        }

        if (added || FileTree.Items.Count == 0)
        {
            RefreshExplorer();
        }

        SelectInTree(fullPath);
        if (focus)
        {
            LeftTabs.SelectedItem = ExplorerTab;
            Dispatcher.UIThread.Post(() => FileTree.Focus(), DispatcherPriority.Background);
        }
    }

    private void SelectInTree(string path)
    {
        if (Find(FileTree.Items.OfType<TreeViewItem>(), path) is { } item)
        {
            FileTree.SelectedItem = item;
            Dispatcher.UIThread.Post(item.BringIntoView, DispatcherPriority.Background);
        }

        static TreeViewItem? Find(IEnumerable<TreeViewItem> items, string path)
        {
            foreach (TreeViewItem item in items)
            {
                if (item.Tag is not ProjectFileEntry entry)
                {
                    continue;
                }

                if (string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }

                if (entry.IsDirectory && item.IsExpanded && path.StartsWith(entry.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && Find((item.ItemsSource ?? item.Items).OfType<TreeViewItem>(), path) is { } inner)
                {
                    return inner;
                }
            }

            return null;
        }
    }

    /// <summary>Quick open (Ctrl+P): type part of a file's name or path and press Enter.</summary>
    private void OnOpenFile(object? sender, RoutedEventArgs e)
    {
        if (workspace is null)
        {
            return;
        }

        string root = workspace.Project.DirectoryPath;
        IReadOnlyList<string> files;
        try
        {
            files = ProjectFiles.ListAll(root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReportError("Could not list the project's files", exception);
            return;
        }

        var search = new TextBox { PlaceholderText = "Type part of a file name", MinWidth = 420 };
        var list = new ListBox { MaxHeight = 360, Background = Brushes.Transparent };
        var flyout = new Flyout
        {
            Placement = PlacementMode.Top,
            Content = new StackPanel { Spacing = 6, Children = { search, list } },
        };
        void Filter()
        {
            string query = search.Text ?? "";
            list.ItemsSource = files
                .Select(file => (file, relative: Path.GetRelativePath(root, file)))
                .Select(file => (file.file, file.relative, rank: SearchMatcher.Rank(Path.GetFileName(file.file), query) ?? (SearchMatcher.Rank(file.relative, query) + 3)))
                .Where(file => file.rank is not null)
                .OrderBy(file => file.rank)
                .ThenBy(file => file.relative, StringComparer.OrdinalIgnoreCase)
                .Take(50)
                .Select(file => new ListBoxItem { Content = file.relative, Tag = file.file })
                .ToArray();
            list.SelectedIndex = 0;
        }

        void Open()
        {
            if (list.SelectedItem is ListBoxItem { Tag: string path })
            {
                flyout.Hide();
                ShowCodeFile(path);
            }
        }

        search.TextChanged += (_, _) => Filter();
        search.KeyDown += (_, args) =>
        {
            int count = list.ItemCount;
            switch (args.Key)
            {
                case Key.Enter:
                    Open();
                    break;
                case Key.Down when count > 0:
                    list.SelectedIndex = (list.SelectedIndex + 1) % count;
                    break;
                case Key.Up when count > 0:
                    list.SelectedIndex = (list.SelectedIndex - 1 + count) % count;
                    break;
                case Key.Escape:
                    flyout.Hide();
                    break;
                default:
                    return;
            }

            args.Handled = true;
        };
        list.DoubleTapped += (_, _) => Open();
        list.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                Open();
                args.Handled = true;
            }
        };
        Filter();
        flyout.Opened += (_, _) => Dispatcher.UIThread.Post(() => search.Focus());
        flyout.ShowAt(StatusText);
    }

    /// <summary>Opens a file of the project in VS Code, in the project folder, at <paramref name="line"/> when given.</summary>
    private void OpenInCode(string path, int? line)
    {
        if (workspace is null)
        {
            return;
        }

        string folder = workspace.Solution is { } solution ? Path.GetDirectoryName(solution)! : workspace.Project.DirectoryPath;
        try
        {
            if (!VisualStudioCode.Open(folder, File.Exists(path) ? path : null, line))
            {
                SetStatus("VS Code was not found. Install it, or set VSIDE_VSCODE to the path of its executable.");
                return;
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            ReportError("Could not start VS Code", exception);
            return;
        }

        SetStatus(File.Exists(path) && workspace.IsCodeFileDirty(path)
            ? $"Opened {Path.GetFileName(path)} in VS Code. It has unsaved changes here; save them (Ctrl+S) to see them there."
            : $"Opened {Path.GetFileName(path)} in VS Code. Changes saved there are picked up here.");
    }

    private void OnOpenFolderInCode(object? sender, RoutedEventArgs e)
    {
        if (workspace is not null)
        {
            OpenInCode(workspace.Project.DirectoryPath, null);
        }
    }

    private void RefreshSolutionPanel()
    {
        SolutionPanel.Children.Clear();
        if (workspace is null)
        {
            return;
        }

        if (workspace.Solution is not { } solution)
        {
            SolutionPanel.Children.Add(new TextBlock
            {
                Text = "No solution file. Use + to create one, so Visual Studio, Rider, and VS Code open the project with others.",
                Classes = { "secondary" },
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(10, 2, 8, 4),
            });
            return;
        }

        Border solutionRow = Row(new TextBlock { Text = $"▣  {Path.GetFileName(solution)}" }, string.Equals(shownFile, solution, StringComparison.OrdinalIgnoreCase),
            $"{solution}\nClick to open it as text.");
        solutionRow.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(solutionRow).Properties.IsLeftButtonPressed)
            {
                ShowCodeFile(solution);
            }
        };
        solutionRow.ContextMenu = new ContextMenu { ItemsSource = SolutionMenuItems(solutionRow) };
        SolutionPanel.Children.Add(solutionRow);
        string current = Path.GetFullPath(workspace.Project.CsprojPath);
        foreach (string csproj in ProjectSolution.ProjectsIn(solution))
        {
            bool isCurrent = string.Equals(csproj, current, StringComparison.OrdinalIgnoreCase);
            bool referenced = workspace.References.Projects.Contains(csproj, StringComparer.OrdinalIgnoreCase);
            string name = Path.GetFileNameWithoutExtension(csproj);
            string detail = isCurrent ? "this project" : referenced ? "referenced" : File.Exists(csproj) ? "" : "missing";
            var content = new DockPanel();
            var note = new TextBlock { Text = detail, Classes = { "secondary" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(note, Dock.Right);
            content.Children.Add(note);
            content.Children.Add(new TextBlock { Text = $"{(isCurrent ? "◉" : "▫")}  {name}", TextTrimming = TextTrimming.CharacterEllipsis });
            Border row = Row(content, selected: false, csproj, indent: 1);
            List<MenuItem> items = [];
            if (isCurrent)
            {
                items.Add(Item("Open Project File", () => ShowCodeFile(csproj)));
            }
            else
            {
                items.Add(Item("Open in VS Code", () => VisualStudioCode.Open(Path.GetDirectoryName(csproj)!, csproj)));
                if (!referenced && File.Exists(csproj))
                {
                    items.Add(Item($"Reference {name} From This Project", () => RunProjectCommand($"dotnet add reference {Path.GetFileName(csproj)}",
                        (project, output, token) => ProjectSolution.AddProjectReferenceAsync(project, csproj, output, token))));
                }

                items.Add(Item("Remove From Solution", () => RunProjectCommand($"dotnet sln remove {Path.GetFileName(csproj)}",
                    (_, output, token) => ProjectSolution.RemoveFromSolutionAsync(solution, csproj, output, token))));
            }

            row.ContextMenu = new ContextMenu { ItemsSource = items };
            SolutionPanel.Children.Add(row);
        }
    }

    private void OnSolutionActions(object? sender, RoutedEventArgs e)
    {
        if (workspace is not null && sender is Control anchor)
        {
            new ContextMenu { ItemsSource = SolutionMenuItems(anchor) }.Open(anchor);
        }
    }

    private MenuItem[] SolutionMenuItems(Control anchor)
    {
        bool hasSolution = workspace?.Solution is not null;
        MenuItem create = Item("Create Solution File", () => OnCreateSolution(this, new RoutedEventArgs()));
        MenuItem add = Item("New Project in Solution…", () => ShowNewSolutionProject(anchor));
        MenuItem existing = Item("Add Existing Project to Solution…", () => OnAddExistingProject(this, new RoutedEventArgs()));
        create.IsEnabled = !hasSolution;
        add.IsEnabled = existing.IsEnabled = hasSolution;
        return [create, add, existing];
    }

    private void OnNewSolutionProject(object? sender, RoutedEventArgs e)
    {
        LeftTabs.SelectedItem = ExplorerTab;
        ShowNewSolutionProject(SolutionActionsButton);
    }

    /// <summary>Asks for a template and a name, then creates the project next to this one and adds it to the solution.</summary>
    private void ShowNewSolutionProject(Control anchor)
    {
        if (workspace?.Solution is not { } solution)
        {
            SetStatus("Create a solution file first.");
            return;
        }

        var template = new ComboBox
        {
            ItemsSource = ProjectSolution.Templates.Select(item => item.Description).ToArray(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var name = new TextBox { PlaceholderText = "Project name, such as Shapes", MinWidth = 260 };
        var reference = new CheckBox { Content = "Reference it from this project (its public methods become nodes)", IsChecked = true };
        var create = new Button { Content = "Create Project", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var message = new TextBlock { Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 };
        var flyout = new Flyout
        {
            Placement = PlacementMode.Right,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "New project in the solution", FontWeight = FontWeight.SemiBold },
                    template,
                    name,
                    reference,
                    message,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { create, cancel } },
                },
            },
        };
        void Submit()
        {
            string projectName = (name.Text ?? "").Trim();
            if (!ProjectSolution.IsValidProjectName(projectName))
            {
                message.Text = "Enter a name of letters, digits, dots, and underscores, starting with a letter.";
                return;
            }

            string shortName = ProjectSolution.Templates[Math.Max(template.SelectedIndex, 0)].ShortName;
            bool addReference = reference.IsChecked == true;
            flyout.Hide();
            RunProjectCommand($"dotnet new {shortName} --name {projectName}",
                (project, output, token) => ProjectSolution.NewProjectAsync(project, solution, shortName, projectName, addReference, output, token));
        }

        create.Click += (_, _) => Submit();
        cancel.Click += (_, _) => flyout.Hide();
        name.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Submit();
                e.Handled = true;
            }
        };
        flyout.Opened += (_, _) => Dispatcher.UIThread.Post(() => name.Focus());
        flyout.ShowAt(anchor);
    }

    private async void OnAddExistingProject(object? sender, RoutedEventArgs e)
    {
        if (workspace?.Solution is not { } solution)
        {
            return;
        }

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a C# project to add to the solution",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("C# project") { Patterns = ["*.csproj"] }],
        });
        if (files is [var file] && file.TryGetLocalPath() is { } path)
        {
            RunProjectCommand($"dotnet sln add {Path.GetFileName(path)}",
                (_, output, token) => ProjectSolution.AddToSolutionAsync(solution, path, output, token));
        }
    }

    /// <summary>Watches the project folder for changes made by other tools; build output and tool folders are ignored.</summary>
    private void WatchProject(VisualProject project)
    {
        StopWatchingProject();
        expandedFolders.Clear();
        restoreOfferedFor = default;
        RefreshExplorer();
        try
        {
            var watcher = new FileSystemWatcher(project.DirectoryPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            };
            string root = project.DirectoryPath;
            string csproj = Path.GetFullPath(project.CsprojPath);
            void Changed(string path)
            {
                if (ProjectFiles.IsInHiddenFolder(root, path) || ProjectFiles.HiddenFolders.Contains(Path.GetFileName(path)))
                {
                    return;
                }

                bool isProjectFile = string.Equals(path, csproj, StringComparison.OrdinalIgnoreCase);
                Dispatcher.UIThread.Post(() =>
                {
                    projectFileChanged |= isProjectFile;
                    diskChangeTimer.Stop();
                    diskChangeTimer.Start();
                });
            }

            watcher.Changed += (_, e) => Changed(e.FullPath);
            watcher.Created += (_, e) => Changed(e.FullPath);
            watcher.Deleted += (_, e) => Changed(e.FullPath);
            watcher.Renamed += (_, e) => Changed(e.FullPath);
            watcher.EnableRaisingEvents = true;
            projectWatcher = watcher;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or PlatformNotSupportedException)
        {
            AppendOutput($"Changes made outside the IDE are not followed ({exception.Message}). Use Reload Files and References.");
        }
    }

    private void StopWatchingProject()
    {
        projectWatcher?.Dispose();
        projectWatcher = null;
        diskChangeTimer.Stop();
        projectFileChanged = false;
    }

    // Brings the Explorer, open files, and references up to date after changes on disk.
    private void OnDiskChanged()
    {
        if (workspace is null)
        {
            return;
        }

        bool reloaded = false;
        foreach (string path in documents.Select(tab => tab.FilePath).OfType<string>())
        {
            try
            {
                reloaded |= workspace.ReloadFromDisk(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A file still being written is read again on its next change.
            }
        }

        workspace.RefreshFiles();
        PruneDocuments();
        RefreshExplorer();
        RefreshProjectPanel();
        UpdateFileBanner();
        UpdateTitle();
        if (reloaded)
        {
            CheckHandwrittenCode();
        }

        if (projectFileChanged)
        {
            projectFileChanged = false;
            ReloadReferencedNodes();
            if (ProjectReferences.NeedsRestore(workspace.Project.CsprojPath))
            {
                RestorePackages();
            }
        }
    }

    /// <summary>
    /// Offers to restore the project's NuGet packages, so their methods become nodes. Restoring runs MSBuild on the project, which
    /// can run code from it, so it only happens when the user agrees (opening a project never runs code). Offered once per change.
    /// </summary>
    private void RestorePackages()
    {
        if (workspace is null)
        {
            return;
        }

        DateTime written = File.GetLastWriteTimeUtc(workspace.Project.CsprojPath);
        if (written == restoreOfferedFor)
        {
            return;
        }

        restoreOfferedFor = written;
        var restore = new Button { Content = "Restore", IsDefault = true };
        var later = new Button { Content = "Not Now", IsCancel = true };
        var flyout = new Flyout
        {
            Placement = PlacementMode.Top,
            Content = new StackPanel
            {
                Spacing = 8,
                MaxWidth = 360,
                Children =
                {
                    new TextBlock { Text = "Restore NuGet packages?", FontWeight = FontWeight.SemiBold },
                    new TextBlock
                    {
                        Text = "The project's packages are not restored, so their methods are not in the palette yet. Restoring runs dotnet restore on the project. Building restores them too.",
                        Classes = { "secondary" },
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { restore, later } },
                },
            },
        };
        restore.Click += (_, _) =>
        {
            flyout.Hide();
            RunProjectCommand("dotnet restore", (project, output, token) => DotNetCli.RestoreAsync(project.CsprojPath, output, token));
        };
        later.Click += (_, _) =>
        {
            flyout.Hide();
            SetStatus("Packages not restored. Build once (Ctrl+Shift+B) to add their methods to the palette.");
        };
        Dispatcher.UIThread.Post(() => flyout.ShowAt(ProjectText), DispatcherPriority.Background);
    }
}
