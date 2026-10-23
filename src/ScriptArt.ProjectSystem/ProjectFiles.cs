using ScriptArt.Compiler.Emit;
using ScriptArt.GraphModel;

namespace ScriptArt.ProjectSystem;

/// <summary>What a file in the project folder is, which decides how the IDE opens it and what may be done to it.</summary>
public enum ProjectFileKind
{
    /// <summary>The <c>.vside</c> project file, which the IDE reads and writes itself.</summary>
    ProjectFile,

    /// <summary>A graph listed in the project; it opens on the canvas.</summary>
    Graph,

    /// <summary>A file the generator owns (it starts with the ownership header); read-only, regenerated from its graph.</summary>
    Generated,

    /// <summary>A handwritten C# file, compiled with the generated code.</summary>
    CSharp,

    /// <summary>Any other text file: the <c>.csproj</c>, solutions, JSON, Markdown, and so on.</summary>
    Text,

    /// <summary>A file that is not text, such as an image or assembly; it is not opened in the editor.</summary>
    Binary,
}

/// <summary>A folder or file in the project folder, as the Explorer lists it.</summary>
/// <param name="Path">Full path.</param>
public sealed record ProjectFileEntry(string Path, string Name, bool IsDirectory);

/// <summary>
/// File management for the Explorer: listing, creating, renaming, deleting, and saving files in the project folder. Every operation
/// stays inside the project folder and refuses what would break the project: the project file, its graphs, and generated files are
/// managed by the IDE and the generator, so they are never renamed, deleted, or overwritten here (D-043).
/// </summary>
public static class ProjectFiles
{
    /// <summary>Folders the Explorer does not list: build output and tool state.</summary>
    public static readonly IReadOnlySet<string> HiddenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs", ".idea", "node_modules", ".godot" };

    // Text files are recognized by extension; anything else with a NUL byte near the start is binary.
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".pdb", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".zip", ".nupkg", ".snk", ".pfx", ".ttf", ".otf", ".woff", ".woff2",
    };

    /// <summary>The folders, then the files, directly in <paramref name="directory"/>, each sorted by name; hidden folders are left out.</summary>
    public static IReadOnlyList<ProjectFileEntry> List(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        IEnumerable<ProjectFileEntry> folders = Directory.EnumerateDirectories(directory)
            .Where(path => !HiddenFolders.Contains(Path.GetFileName(path)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(path => new ProjectFileEntry(path, Path.GetFileName(path), IsDirectory: true));
        IEnumerable<ProjectFileEntry> files = Directory.EnumerateFiles(directory)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(path => new ProjectFileEntry(path, Path.GetFileName(path), IsDirectory: false));
        return [.. folders, .. files];
    }

    /// <summary>Every file under <paramref name="directory"/>, outside hidden folders, sorted by path; for opening a file by name.</summary>
    public static IReadOnlyList<string> ListAll(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        string root = Path.GetFullPath(directory);
        return [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(file => !IsInHiddenFolder(root, file))
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>True when <paramref name="path"/> is inside one of the <see cref="HiddenFolders"/> under <paramref name="root"/>.</summary>
    public static bool IsInHiddenFolder(string root, string path) =>
        Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).SkipLast(1).Any(HiddenFolders.Contains);

    /// <summary>What <paramref name="path"/> is to <paramref name="project"/>.</summary>
    public static ProjectFileKind KindOf(VisualProject project, string path)
    {
        ArgumentNullException.ThrowIfNull(project);
        string fullPath = Path.GetFullPath(path);
        if (string.Equals(fullPath, Path.GetFullPath(project.ProjectFilePath), StringComparison.OrdinalIgnoreCase))
        {
            return ProjectFileKind.ProjectFile;
        }

        if (project.Graphs.Any(graph => string.Equals(Path.GetFullPath(Path.Combine(project.DirectoryPath, graph.RelativePath)), fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            return ProjectFileKind.Graph;
        }

        string extension = Path.GetExtension(fullPath);
        if (BinaryExtensions.Contains(extension) || (File.Exists(fullPath) && LooksBinary(fullPath)))
        {
            return ProjectFileKind.Binary;
        }

        if (File.Exists(fullPath) && StartsWithOwnershipHeader(fullPath))
        {
            return ProjectFileKind.Generated;
        }

        return string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase) ? ProjectFileKind.CSharp : ProjectFileKind.Text;
    }

    /// <summary>
    /// Why <paramref name="path"/> may not be renamed or deleted, or null when it may. A folder is protected when it holds a protected
    /// file, and the project folder itself always is.
    /// </summary>
    public static string? ProtectionOf(VisualProject project, string path)
    {
        ArgumentNullException.ThrowIfNull(project);
        string fullPath = Inside(project, path);
        if (string.Equals(fullPath, Path.GetFullPath(project.DirectoryPath), StringComparison.OrdinalIgnoreCase))
        {
            return "This is the project folder.";
        }

        if (Directory.Exists(fullPath))
        {
            return Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories)
                .Where(file => !IsInHiddenFolder(fullPath, file))
                .Select(file => ProtectionOf(project, file))
                .FirstOrDefault(reason => reason is not null) is { } inner
                ? $"The folder holds a file the IDE manages. {inner}"
                : null;
        }

        if (string.Equals(fullPath, Path.GetFullPath(project.CsprojPath), StringComparison.OrdinalIgnoreCase))
        {
            return $"{Path.GetFileName(fullPath)} builds the project; it can be edited but not renamed or deleted here.";
        }

        return KindOf(project, fullPath) switch
        {
            ProjectFileKind.ProjectFile => "The project file is managed by the IDE.",
            ProjectFileKind.Graph => "Graphs are renamed and deleted in My Project, which keeps the project file and calls to them in step.",
            ProjectFileKind.Generated => "Generated files are owned by the generator; change their graph instead.",
            _ => null,
        };
    }

    /// <summary>Creates an empty file <paramref name="name"/> in <paramref name="directory"/> and returns its full path.</summary>
    public static string CreateFile(VisualProject project, string directory, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.EndsWith(".vside", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".graph.json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Visual scripts are graphs, not files: add a function, class, or struct graph instead. A .vside file is a whole project.", nameof(name));
        }

        string path = NewPath(project, directory, name);
        AtomicFile.WriteAllText(path, string.Equals(Path.GetExtension(name), ".cs", StringComparison.OrdinalIgnoreCase)
            ? $"namespace {project.Document.RootNamespace};\n"
            : "");
        return path;
    }

    /// <summary>Creates a folder <paramref name="name"/> in <paramref name="directory"/> and returns its full path.</summary>
    public static string CreateFolder(VisualProject project, string directory, string name)
    {
        string path = NewPath(project, directory, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Renames a file or folder in place and returns its new full path. Refuses protected items and existing names.</summary>
    public static string Rename(VisualProject project, string path, string newName)
    {
        string fullPath = Inside(project, path);
        if (ProtectionOf(project, fullPath) is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        string target = NewPath(project, Path.GetDirectoryName(fullPath)!, newName);
        if (Directory.Exists(fullPath))
        {
            Directory.Move(fullPath, target);
        }
        else
        {
            File.Move(fullPath, target);
        }

        return target;
    }

    /// <summary>Deletes a file, or a folder with everything in it. Refuses protected items. This cannot be undone.</summary>
    public static void Delete(VisualProject project, string path)
    {
        string fullPath = Inside(project, path);
        if (ProtectionOf(project, fullPath) is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
        else
        {
            File.Delete(fullPath);
        }
    }

    /// <summary>
    /// Saves text edited in the IDE. C# files go through <see cref="VisualProject.SaveSourceFile"/>; the project file, graphs, and
    /// generated files are refused, as is text that starts with the generator's header.
    /// </summary>
    public static void SaveText(VisualProject project, string path, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string fullPath = Inside(project, path);
        switch (KindOf(project, fullPath))
        {
            case ProjectFileKind.CSharp:
                project.SaveSourceFile(fullPath, text);
                return;
            case ProjectFileKind.ProjectFile or ProjectFileKind.Graph:
                throw new InvalidOperationException($"'{Path.GetFileName(fullPath)}' is managed by the IDE; edit it through the canvas and My Project.");
            case ProjectFileKind.Generated:
                throw new InvalidOperationException($"'{Path.GetFileName(fullPath)}' is generated from a graph. Edit the graph instead.");
            case ProjectFileKind.Binary:
                throw new InvalidOperationException($"'{Path.GetFileName(fullPath)}' is not a text file.");
        }

        if (GeneratedSource.IsGeneratedText(text))
        {
            throw new InvalidOperationException($"'{Path.GetFileName(fullPath)}' starts with the generator's header, so generating code could overwrite or delete it. Remove the header to save it.");
        }

        AtomicFile.WriteAllText(fullPath, text);
    }

    /// <summary>True for a file or folder name: not empty, not <c>.</c> or <c>..</c>, and without path separators or invalid characters.</summary>
    public static bool IsValidName(string? name) =>
        name is { Length: > 0 and <= 200 } && name.Trim() == name && name is not "." and not ".."
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) < 0;

    // The full path of a new item, checked to be a valid name inside the project that does not exist yet.
    private static string NewPath(VisualProject project, string directory, string name)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException($"'{name}' is not a valid file or folder name.", nameof(name));
        }

        string parent = Inside(project, directory);
        if (!Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException($"'{directory}' is not a folder of the project.");
        }

        string path = Path.Combine(parent, name);
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException($"'{name}' already exists. Choose a different name.");
        }

        return path;
    }

    // The full path of an item in the project folder; anything outside it is refused.
    private static string Inside(VisualProject project, string path)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(path);
        string root = Path.GetFullPath(project.DirectoryPath);
        string fullPath = Path.GetFullPath(path);
        if (!string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            && !fullPath.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{path}' is outside the project folder.", nameof(path));
        }

        return fullPath;
    }

    private static bool StartsWithOwnershipHeader(string path)
    {
        using var reader = new StreamReader(path);
        char[] buffer = new char[512];
        int read = reader.ReadBlock(buffer);
        return GeneratedSource.IsGeneratedText(new string(buffer, 0, read));
    }

    private static bool LooksBinary(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] buffer = new byte[4096];
        int read = stream.Read(buffer);
        return Array.IndexOf(buffer, (byte)0, 0, read) >= 0;
    }
}
