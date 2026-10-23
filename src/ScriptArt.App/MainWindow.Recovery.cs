using Avalonia.Threading;
using ScriptArt.ProjectSystem;

namespace ScriptArt.App;

/// <summary>
/// Autosave and recovery (D-045). Every autosave interval (Preferences), unsaved graphs, the project file, and open files are copied
/// to the recovery folder, never over the project's files. Saving or discarding removes the copies. When a project opens with copies
/// newer than its files (after a crash or a forced kill), they are shown in the diff view to restore or discard.
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer autosaveTimer = new();
    private ProjectRecovery? recovery;

    // True while the diff view shows recovered files instead of a generation preview.
    private bool showingRecovery;

    // Where recovery copies are kept; UI tests point it at a temporary folder.
    internal static string RecoveryRoot { get; set; } = ProjectRecovery.DefaultRoot;

    private void InitializeAutosave() => autosaveTimer.Tick += (_, _) => Autosave();

    /// <summary>Starts or stops the timer for the open project with the interval from the preferences.</summary>
    private void ScheduleAutosave()
    {
        autosaveTimer.Stop();
        if (recovery is not null && preferences.AutosaveIntervalSeconds > 0)
        {
            autosaveTimer.Interval = TimeSpan.FromSeconds(preferences.AutosaveIntervalSeconds);
            autosaveTimer.Start();
        }
    }

    /// <summary>Writes recovery copies of the unsaved work, or removes them when there is none.</summary>
    internal void Autosave()
    {
        if (workspace is null || recovery is null)
        {
            return;
        }

        try
        {
            if (!workspace.IsDirty)
            {
                recovery.Clear();
                return;
            }

            Dictionary<string, string> files = new(workspace.Project.SerializedFiles(), StringComparer.OrdinalIgnoreCase);
            foreach ((string path, string text) in workspace.UnsavedFiles)
            {
                files[path] = text;
            }

            recovery.Write(files);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            // Autosave runs unattended: report the problem without interrupting, and try again at the next interval.
            AppendOutput($"Autosave could not write recovery copies: {exception.Message}");
        }
    }

    /// <summary>Starts recovery for a newly opened project and offers copies left by an earlier session.</summary>
    private void OpenRecovery(VisualProject project)
    {
        recovery = new ProjectRecovery(RecoveryRoot, project.ProjectFilePath);
        ScheduleAutosave();
        IReadOnlyList<GeneratedFileChange> changes;
        try
        {
            changes = recovery.Changes();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppendOutput($"Could not read the recovery copies in {recovery.Folder}: {exception.Message}");
            return;
        }

        if (changes.Count > 0)
        {
            showingRecovery = true;
            ShowDiffPreview(project, changes);
            DiffTitle.Text = $"Recover unsaved work? {changes.Count} file{(changes.Count == 1 ? "" : "s")} from an earlier session";
            DiffNote.Text = "ScriptArt Code closed without saving these changes. Restore writes them to the project's files; Discard deletes the copies. Each file shows how restoring would change it.";
            WriteDiffButton.Content = "Restore";
            CancelDiffButton.Content = "Discard";
            SetStatus("Unsaved work from an earlier session was found. Restore or discard it.");
        }
    }

    /// <summary>Removes the recovery copies: the work was saved or deliberately discarded.</summary>
    private void ClearRecovery()
    {
        try
        {
            recovery?.Clear();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppendOutput($"Could not remove the recovery copies in {recovery?.Folder}: {exception.Message}");
        }
    }

    private void RestoreRecovery()
    {
        if (workspace is null || recovery is null)
        {
            return;
        }

        string projectFile = workspace.Project.ProjectFilePath;
        try
        {
            recovery.Restore();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReportError("Could not restore the recovered files", exception);
            return;
        }

        OpenProjectFile(projectFile);
        SetStatus("Restored the unsaved work from the earlier session.");
    }

    private void EndRecoveryPreview()
    {
        showingRecovery = false;
        DiffNote.Text = GenerationPreviewNote;
        WriteDiffButton.Content = "Write Files";
        CancelDiffButton.Content = "Cancel";
    }
}
