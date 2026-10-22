using Avalonia.Input.Platform;
using VisualStudioLikeIDE.GraphModel;
using VisualStudioLikeIDE.NodeEditor;

namespace VisualStudioLikeIDE.App;

/// <summary>Cut, copy, paste, and duplicate through the system clipboard.</summary>
public sealed partial class MainWindow
{
    private async Task CopySelectionAsync(bool cut)
    {
        if (workspace is null || GraphCanvas.SelectedItems.Count == 0 || Clipboard is null)
        {
            return;
        }

        string[] ids = [.. GraphCanvas.SelectedItems];
        try
        {
            await Clipboard.SetTextAsync(GraphSerializer.Serialize(workspace.Editor.CopyFragment(ids)));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ReportError("Copy", exception);
            return;
        }

        if (cut)
        {
            workspace.Editor.Delete(ids);
        }

        SetStatus($"{(cut ? "Cut" : "Copied")} {ids.Length} item{(ids.Length == 1 ? "" : "s")}.");
    }

    private async Task PasteAsync()
    {
        if (workspace is null || Clipboard is null)
        {
            return;
        }

        // Clipboard text comes from anywhere: only a well-formed fragment is accepted, and the editor drops dangling connections.
        GraphDocument? fragment = null;
        try
        {
            if (await Clipboard.TryGetTextAsync() is { } text)
            {
                fragment = GraphSerializer.Deserialize(text);
            }
        }
        catch (GraphFormatException)
        {
        }

        if (fragment?.Id != GraphEditor.FragmentId || (fragment.Nodes.Count == 0 && fragment.Comments.Count == 0))
        {
            SetStatus("The clipboard does not contain nodes.");
            return;
        }

        PasteFragment(fragment, GraphCanvas.PointerWorld ?? GraphCanvas.ViewCenterWorld);
    }

    private void PasteFragment(GraphDocument fragment, Avalonia.Point at)
    {
        if (workspace is null)
        {
            return;
        }

        PasteResult pasted = workspace.Editor.Paste(fragment, at.X, at.Y);
        GraphCanvas.SelectItems(pasted.Ids);
        GraphCanvas.Focus();
        int count = pasted.Nodes.Count + pasted.Comments.Count;
        SetStatus($"Pasted {count} item{(count == 1 ? "" : "s")}.");
    }
}
