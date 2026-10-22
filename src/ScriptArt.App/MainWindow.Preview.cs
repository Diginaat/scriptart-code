using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using ScriptArt.Compiler;
using ScriptArt.ProjectSystem;

namespace ScriptArt.App;

/// <summary>
/// The generation preview (F6 with Build → Preview Changes Before Generating): the generated files that would be added, changed, or
/// deleted, each as a colored unified diff, written only when confirmed.
/// </summary>
public partial class MainWindow
{
    private void InitializeDiffPreview()
    {
        DiffEditor.TextArea.TextView.LineTransformers.Add(new DiffColorizer());
        DiffFileList.SelectionChanged += (_, _) =>
        {
            if (DiffFileList.SelectedItem is ListBoxItem { Tag: (GeneratedFileChange change, string name) })
            {
                DiffEditor.Text = change.UnifiedDiff(name);
                DiffEditor.ScrollToHome();
            }
        };
    }

    /// <summary>Verifies the project and shows what generation would change; writes nothing.</summary>
    private async Task PreviewGenerationAsync()
    {
        if (workspace is null || busy)
        {
            return;
        }

        recompileTimer.Stop();
        Recompile();
        SetBusy(true, "Verifying C# and comparing with the generated files…");
        try
        {
            VisualProject project = workspace.Project;
            GraphCompiler compiler = workspace.Compiler;
            IReadOnlyDictionary<string, string> unsaved = workspace.UnsavedFiles;
            ProjectGenerationResult result = await Task.Run(() => project.PreviewGeneration(compiler, unsaved));
            ShowProblems(result.Diagnostics);
            if (!result.Success)
            {
                BottomTabs.SelectedItem = ProblemsTab;
                SetStatus("Generation stopped: fix the problems listed below. No files were written.");
            }
            else if (result.Changes.Count == 0)
            {
                workspace.GeneratedSources = result.Sources;
                SetStatus("Generated code is up to date.");
            }
            else
            {
                ShowDiffPreview(project, result.Changes);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReportError("Could not compare the generated code", exception);
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void ShowDiffPreview(VisualProject project, IReadOnlyList<GeneratedFileChange> changes)
    {
        DiffFileList.Items.Clear();
        foreach (GeneratedFileChange change in changes)
        {
            string name = Path.GetRelativePath(project.DirectoryPath, change.Path).Replace('\\', '/');
            (string mark, string brush) = change.Kind switch
            {
                GeneratedFileChangeKind.Added => ("Added", "#5CCB7A"),
                GeneratedFileChangeKind.Deleted => ("Deleted", "#F06A6A"),
                _ => ("Changed", "#E8B44C"),
            };
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = mark, Width = 58, Foreground = new SolidColorBrush(Color.Parse(brush)) });
            row.Children.Add(new TextBlock { Text = name });
            DiffFileList.Items.Add(new ListBoxItem { Content = row, Tag = (change, name) });
        }

        DiffTitle.Text = $"Generating would change {changes.Count} file{(changes.Count == 1 ? "" : "s")}";
        DiffPreview.IsVisible = true;
        DiffFileList.SelectedIndex = 0;
        WriteDiffButton.Focus();
        SetStatus("Review the changes, then write them or cancel.");
    }

    private void HideDiffPreview()
    {
        DiffPreview.IsVisible = false;
        DiffFileList.Items.Clear();
        DiffEditor.Text = "";
        GraphCanvas.Focus();
    }

    private async void OnWriteGeneratedFiles(object? sender, RoutedEventArgs e)
    {
        HideDiffPreview();
        await GenerateAsync(showOutput: false);
    }

    private void OnCancelDiff(object? sender, RoutedEventArgs e)
    {
        HideDiffPreview();
        SetStatus("Nothing was written.");
    }

    /// <summary>Colors a unified diff: added lines green, removed lines red, hunk headers blue.</summary>
    private sealed class DiffColorizer : DocumentColorizingTransformer
    {
        private static readonly IBrush Added = new SolidColorBrush(Color.Parse("#7FD68F"));
        private static readonly IBrush AddedBackground = new SolidColorBrush(Color.Parse("#1F3A27"));
        private static readonly IBrush Removed = new SolidColorBrush(Color.Parse("#F08A8A"));
        private static readonly IBrush RemovedBackground = new SolidColorBrush(Color.Parse("#3F2326"));
        private static readonly IBrush Header = new SolidColorBrush(Color.Parse("#6CA6FF"));

        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.Length == 0)
            {
                return;
            }

            string start = CurrentContext.Document.GetText(line.Offset, Math.Min(3, line.Length));
            (IBrush? foreground, IBrush? background) = start switch
            {
                "+++" or "---" or ['@', '@', ..] => (Header, null),
                ['+', ..] => (Added, AddedBackground),
                ['-', ..] => (Removed, RemovedBackground),
                _ => ((IBrush?)null, (IBrush?)null),
            };
            if (foreground is null)
            {
                return;
            }

            ChangeLinePart(line.Offset, line.EndOffset, element =>
            {
                element.TextRunProperties.SetForegroundBrush(foreground);
                if (background is not null)
                {
                    element.BackgroundBrush = background;
                }
            });
        }
    }
}
