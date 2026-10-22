using System.Text;

namespace VisualStudioLikeIDE.GraphModel;

/// <summary>
/// Writes files so that a crash or I/O failure never leaves a half-written document behind.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void WriteAllText(string path, string contents)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = fullPath + ".tmp";
        File.WriteAllText(temporaryPath, contents, Utf8NoBom);
        File.Move(temporaryPath, fullPath, overwrite: true);
    }
}
