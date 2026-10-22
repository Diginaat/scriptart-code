namespace VisualStudioLikeIDE.Tests;

/// <summary>A unique directory under the system temp folder, deleted on dispose.</summary>
internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VisualStudioLikeIDE.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a lingering build server may still hold a handle.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
