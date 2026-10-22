using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ScriptArt.ProjectSystem;

namespace ScriptArt.App;

/// <summary>
/// The Files and References sections of My Project and the Project menu: handwritten C# files, NuGet packages, project references,
/// and the solution file. References change through the standard <c>dotnet</c> commands, with their output in Output.
/// </summary>
public partial class MainWindow
{
    private const string FilesSection = "Files";
    private const string ReferencesSection = "References";

    // True while the Files section shows a name box for a new file.
    private bool addingFile;

    private void AddFileSections(Func<string, bool> matches)
    {
        if (workspace is null)
        {
            return;
        }

        List<Control> files = [];
        if (addingFile)
        {
            // The box lasts until the panel is next rebuilt, which also happens when it is confirmed or cancelled.
            addingFile = false;
            files.Add(Row(NameBox("", AddSourceFile, "C#  "), selected: false, "A class name; the file is created as Name.cs in the project folder."));
        }

        foreach (string path in workspace.SourceFiles)
        {
            string name = Path.GetRelativePath(workspace.Project.DirectoryPath, path);
            if (matches(name))
            {
                files.Add(FileRow(path, name));
            }
        }

        AddSection(FilesSection, OnAddSourceFile, files);

        List<Control> references = [];
        if (workspace.Solution is { } solution)
        {
            references.Add(Row(new TextBlock { Text = $"▤  {Path.GetFileName(solution)}" }, selected: false, $"{solution}\nThe solution file, for Visual Studio, Rider, and VS Code."));
        }

        foreach (PackageReferenceItem package in workspace.References.Packages.Where(package => matches(package.Id)))
        {
            references.Add(ReferenceRow($"◫  {package.Id}", package.Version, $"NuGet package {package.Id} {package.Version}".TrimEnd(),
                $"Remove {package.Id}", () => RunProjectCommand($"dotnet remove package {package.Id}",
                    (project, output, token) => ProjectSolution.RemovePackageAsync(project, package.Id, output, token))));
        }

        foreach (string referenced in workspace.References.Projects.Where(path => matches(Path.GetFileNameWithoutExtension(path))))
        {
            references.Add(ReferenceRow($"▣  {Path.GetFileNameWithoutExtension(referenced)}", null, $"Project reference\n{referenced}",
                $"Remove {Path.GetFileName(referenced)}", () => RunProjectCommand($"dotnet remove reference {Path.GetFileName(referenced)}",
                    (project, output, token) => ProjectSolution.RemoveProjectReferenceAsync(project, referenced, output, token))));
        }

        AddSection(ReferencesSection, OnAddReference, references);
    }

    private Border FileRow(string path, string name)
    {
        bool dirty = workspace!.IsCodeFileDirty(path);
        Border row = Row(new TextBlock { Text = $"C#  {name}{(dirty ? " •" : "")}" },
            string.Equals(shownFile, path, StringComparison.OrdinalIgnoreCase), $"{path}\nHandwritten C#: click to edit it in the Code tab.");
        row.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
            {
                ShowCodeFile(path);
            }
        };
        row.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ShowCodeFile(path);
                e.Handled = true;
            }
        };
        row.ContextMenu = new ContextMenu { ItemsSource = new[] { Item("Open", () => ShowCodeFile(path)) } };
        return row;
    }

    private Border ReferenceRow(string text, string? detail, string tip, string removeHeader, Action remove)
    {
        var content = new DockPanel();
        if (detail is not null)
        {
            var version = new TextBlock { Text = detail, Classes = { "secondary" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(version, Dock.Right);
            content.Children.Add(version);
        }

        content.Children.Add(new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis });
        Border row = Row(content, selected: false, tip);
        row.ContextMenu = new ContextMenu { ItemsSource = new[] { Item(removeHeader, remove) } };
        return row;
    }

    private void OnAddSourceFile(object? sender, RoutedEventArgs e)
    {
        if (workspace is null)
        {
            return;
        }

        LeftTabs.SelectedIndex = 0;
        collapsedSections.Remove(FilesSection);
        addingFile = true;
        RefreshProjectPanel();
    }

    private void AddSourceFile(string className)
    {
        if (workspace is null)
        {
            return;
        }

        try
        {
            string path = workspace.Project.AddSourceFile(className);
            workspace.RefreshFiles();
            ShowCodeFile(path, line: 5);
            SetStatus($"Created {Path.GetFileName(path)}. It is compiled with the generated code and never changed by the generator.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ReportError("Could not create the file", exception);
        }
    }

    private void OnAddReference(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor)
        {
            return;
        }

        var menu = new ContextMenu
        {
            ItemsSource = new[]
            {
                Item("Add NuGet Package…", () => ShowAddPackage(anchor)),
                Item("Add Project Reference…", () => OnAddProjectReference(this, e)),
                Item("Create Solution File", () => OnCreateSolution(this, e)),
            },
        };
        menu.Open(anchor);
    }

    private void OnAddPackage(object? sender, RoutedEventArgs e)
    {
        LeftTabs.SelectedIndex = 0;
        ShowAddPackage(ProjectPanel);
    }

    /// <summary>Asks for a package id and an optional version, then runs <c>dotnet add package</c>.</summary>
    private void ShowAddPackage(Control anchor)
    {
        var id = new TextBox { PlaceholderText = "Package id, such as Humanizer", MinWidth = 260 };
        var version = new TextBox { PlaceholderText = "Version (empty for the latest)" };
        var add = new Button { Content = "Add Package", IsDefault = true };
        var cancel = new Button { Content = "Cancel" };
        var message = new TextBlock { Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 };
        var flyout = new Flyout
        {
            Placement = PlacementMode.Right,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Add a NuGet package", FontWeight = FontWeight.SemiBold },
                    id,
                    version,
                    message,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { add, cancel } },
                },
            },
        };
        void Submit()
        {
            string packageId = (id.Text ?? "").Trim();
            string? packageVersion = string.IsNullOrWhiteSpace(version.Text) ? null : version.Text.Trim();
            if (!ProjectSolution.IsValidPackageId(packageId))
            {
                message.Text = "Enter a package id: letters, digits, dots, hyphens, and underscores.";
                return;
            }

            if (packageVersion is not null && !ProjectSolution.IsValidVersion(packageVersion))
            {
                message.Text = "Enter a version such as 2.14.1, or leave it empty for the latest.";
                return;
            }

            flyout.Hide();
            RunProjectCommand($"dotnet add package {packageId}{(packageVersion is null ? "" : " --version " + packageVersion)}",
                (project, output, token) => ProjectSolution.AddPackageAsync(project, packageId, packageVersion, output, token));
        }

        add.Click += (_, _) => Submit();
        cancel.Click += (_, _) => flyout.Hide();
        id.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Submit();
                e.Handled = true;
            }
        };
        flyout.Opened += (_, _) => Dispatcher.UIThread.Post(() => id.Focus());
        flyout.ShowAt(anchor);
    }

    private async void OnAddProjectReference(object? sender, RoutedEventArgs e)
    {
        if (workspace is null)
        {
            return;
        }

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a C# project to reference",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("C# project") { Patterns = ["*.csproj"] }],
        });
        if (files is [var file] && file.TryGetLocalPath() is { } path)
        {
            RunProjectCommand($"dotnet add reference {Path.GetFileName(path)}",
                (project, output, token) => ProjectSolution.AddProjectReferenceAsync(project, path, output, token));
        }
    }

    private void OnCreateSolution(object? sender, RoutedEventArgs e) =>
        RunProjectCommand("dotnet new sln", (project, output, token) => ProjectSolution.CreateAsync(project, output, token));

    private void OnRefreshFiles(object? sender, RoutedEventArgs e)
    {
        if (workspace is null)
        {
            return;
        }

        workspace.RefreshFiles();
        ReloadReferencedNodes();
        RefreshProjectPanel();
        UpdateCommandStates();
        CheckHandwrittenCode();
        SetStatus("Reloaded the project's files and references.");
    }

    // Adds nodes for assemblies the project started referencing.
    private void ReloadReferencedNodes()
    {
        if (workspace?.LoadNewReferences() == true)
        {
            paletteVariables = "";
            RefreshPaletteForVariables();
            Recompile();
            AppendOutput("Added nodes for the methods of newly referenced assemblies (palette category .NET).");
        }
    }

    /// <summary>Runs a solution or reference command with its output in Output, then reloads the files and references.</summary>
    private async void RunProjectCommand(string description, Func<VisualProject, Action<string>, CancellationToken, Task<int>> command)
    {
        if (workspace is null || busy || runningProgram is not null || debugSession is not null)
        {
            SetStatus("Wait until the build, run, or debug session ends.");
            return;
        }

        VisualProject project = workspace.Project;
        BottomTabs.SelectedItem = OutputTab;
        AppendOutput("> " + description);
        SetBusy(true, description + "…");
        buildCancellation = new CancellationTokenSource();
        try
        {
            int exitCode = await command(project, line => Dispatcher.UIThread.Post(() => AppendOutput(line)), buildCancellation.Token);
            SetStatus(exitCode == 0 ? $"Done: {description}." : $"{description} failed (exit code {exitCode}). See Output.");
        }
        catch (OperationCanceledException)
        {
            AppendOutput("Cancelled.");
            SetStatus("Cancelled.");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ReportError(description, exception);
        }
        finally
        {
            buildCancellation.Dispose();
            buildCancellation = null;
            if (workspace?.Project == project)
            {
                workspace.RefreshFiles();
                ReloadReferencedNodes();
            }

            SetBusy(false, null);
            RefreshProjectPanel();
        }
    }
}
