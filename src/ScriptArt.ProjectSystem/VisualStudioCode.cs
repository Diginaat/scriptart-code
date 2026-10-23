using System.Diagnostics;

namespace ScriptArt.ProjectSystem;

/// <summary>
/// Opens folders and files in Visual Studio Code, for work the IDE does not do itself. VS Code is found through
/// <c>VSIDE_VSCODE</c>, the <c>code</c> command on <c>PATH</c>, or its default install folders; it is started with an argument
/// list, never through a shell.
/// </summary>
public static class VisualStudioCode
{
    /// <summary>The VS Code executable, or null when it is not installed where the IDE can find it.</summary>
    public static string? Find() => Find(
        Environment.GetEnvironmentVariable("VSIDE_VSCODE"),
        Environment.GetEnvironmentVariable("PATH"),
        OperatingSystem.IsWindows(),
        File.Exists);

    /// <summary>Finds VS Code given the environment; separated from <see cref="Find()"/> for tests.</summary>
    internal static string? Find(string? overridePath, string? pathVariable, bool windows, Func<string, bool> exists)
    {
        if (overridePath is { Length: > 0 } && exists(overridePath))
        {
            return overridePath;
        }

        foreach (string directory in (pathVariable ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (windows)
            {
                // PATH holds "Microsoft VS Code\bin\code.cmd", a script; start the executable next to its folder instead.
                if (exists(Path.Combine(directory, "code.cmd")) && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory)) is { } install
                    && Path.Combine(install, "Code.exe") is var executable && exists(executable))
                {
                    return executable;
                }
            }
            else if (Path.Combine(directory, "code") is var command && exists(command))
            {
                return command;
            }
        }

        string[] defaults = windows
            ?
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"),
            ]
            : ["/usr/bin/code", "/usr/local/bin/code", "/snap/bin/code", "/Applications/Visual Studio Code.app/Contents/Resources/app/bin/code"];
        return defaults.FirstOrDefault(exists);
    }

    /// <summary>
    /// Opens <paramref name="folder"/> in VS Code, and <paramref name="file"/> in it when given (at <paramref name="line"/>).
    /// Returns false when VS Code is not found.
    /// </summary>
    public static bool Open(string folder, string? file = null, int? line = null)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (Find() is not { } executable)
        {
            return false;
        }

        var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false };
        startInfo.ArgumentList.Add(Path.GetFullPath(folder));
        if (file is not null)
        {
            startInfo.ArgumentList.Add("--goto");
            startInfo.ArgumentList.Add(Path.GetFullPath(file) + (line is { } target ? ":" + target.ToString(System.Globalization.CultureInfo.InvariantCulture) : ""));
        }

        using Process? process = Process.Start(startInfo);
        return process is not null;
    }
}
