using System.Diagnostics;

namespace ScriptArt.ProjectSystem;

/// <summary>
/// Runs the standard .NET SDK (<c>dotnet build</c>, <c>dotnet run</c>) on generated projects.
/// Arguments are passed as a list, never as a concatenated command line, so paths cannot inject options.
/// </summary>
public static class DotNetCli
{
    // Variables set when this process itself runs under the SDK (for example under dotnet test). Inheriting them would
    // point the child build at the parent's MSBuild instead of resolving the SDK normally.
    private static readonly string[] InheritedMsBuildVariables =
        ["MSBuildExtensionsPath", "MSBUILD_EXE_PATH", "MSBuildSDKsPath", "MSBuildLoadMicrosoftTargetsReadOnly"];

    public static Task<int> BuildAsync(string csprojPath, string configuration, Action<string> output, CancellationToken cancellationToken) =>
        RunAsync(["build", csprojPath, "--nologo", "-c", configuration], output, cancellationToken);

    /// <summary>Restores the project's NuGet packages, which records their assemblies in <c>obj/project.assets.json</c>.</summary>
    public static Task<int> RestoreAsync(string csprojPath, Action<string> output, CancellationToken cancellationToken) =>
        RunAsync(["restore", csprojPath], output, cancellationToken);

    /// <summary>Runs <c>dotnet</c> with <paramref name="arguments"/> to the end and returns its exit code; cancelling stops it.</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> arguments, Action<string> output, CancellationToken cancellationToken)
    {
        using DotNetProcess process = DotNetProcess.Start(arguments, output);
        try
        {
            return await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Stop();
            throw;
        }
    }

    /// <summary>Starts the already-built project. Standard input stays open so the IDE can forward lines to it.</summary>
    public static DotNetProcess StartRun(string csprojPath, string configuration, Action<string> output) =>
        DotNetProcess.Start(["run", "--project", csprojPath, "--no-build", "-c", configuration], output);

    internal static ProcessStartInfo CreateStartInfo(IEnumerable<string> arguments)
    {
        string executable = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host)
            ? host
            : "dotnet";
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (string variable in InheritedMsBuildVariables)
        {
            startInfo.Environment.Remove(variable);
        }

        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        return startInfo;
    }
}

/// <summary>A running <c>dotnet</c> process, or a Godot game, whose output is forwarded line by line.</summary>
public sealed class DotNetProcess : IDisposable
{
    private readonly Process process;

    private DotNetProcess(Process process)
    {
        this.process = process;
    }

    /// <param name="environment">Variables to set for the process, on top of the IDE's own environment.</param>
    public static DotNetProcess Start(IEnumerable<string> arguments, Action<string> output, IReadOnlyDictionary<string, string>? environment = null)
    {
        ProcessStartInfo startInfo = DotNetCli.CreateStartInfo(arguments);
        foreach ((string name, string value) in environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[name] = value;
        }

        return Start(startInfo, output, "The .NET SDK ('dotnet') was not found. Install the .NET 10 SDK and make sure it is on PATH.");
    }

    /// <summary>
    /// Runs the Godot project in <paramref name="projectDirectory"/> (the folder of <c>project.godot</c>) with the Godot 4 .NET
    /// executable at <paramref name="godotPath"/>, which starts its main scene. Build the project first: Godot loads the assembly
    /// <c>dotnet build</c> writes under <c>.godot/mono/temp/bin</c>.
    /// </summary>
    /// <param name="arguments">More of Godot's command-line options, such as <c>--headless</c>.</param>
    public static DotNetProcess StartGodot(string godotPath, string projectDirectory, Action<string> output, IEnumerable<string>? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(godotPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        var startInfo = new ProcessStartInfo(godotPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = projectDirectory,
        };
        startInfo.ArgumentList.Add("--path");
        startInfo.ArgumentList.Add(projectDirectory);
        foreach (string argument in arguments ?? [])
        {
            startInfo.ArgumentList.Add(argument);
        }
        return Start(startInfo, output, $"Godot could not be started from '{godotPath}'. Choose the Godot 4 .NET executable in Tools → Preferences.");
    }

    private static DotNetProcess Start(ProcessStartInfo startInfo, Action<string> output, string notFound)
    {
        ArgumentNullException.ThrowIfNull(output);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                output(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                output(e.Data);
            }
        };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            process.Dispose();
            throw new InvalidOperationException(notFound, exception);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new DotNetProcess(process);
    }

    public bool HasExited => process.HasExited;

    /// <summary>The operating system's id for the process.</summary>
    public int Id => process.Id;

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }

    /// <summary>Sends one line to the process's standard input.</summary>
    public void WriteInput(string line)
    {
        if (!process.HasExited)
        {
            process.StandardInput.WriteLine(line);
            process.StandardInput.Flush();
        }
    }

    /// <summary>Closes standard input, which the program reads as end of input.</summary>
    public void CloseInput()
    {
        if (!process.HasExited)
        {
            process.StandardInput.Close();
        }
    }

    /// <summary>Terminates the process and any child processes it started.</summary>
    public void Stop()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the check and the kill.
        }
    }

    public void Dispose() => process.Dispose();
}
