using System.Diagnostics;

namespace VisualStudioLikeIDE.ProjectSystem;

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
        RunToEndAsync(["build", csprojPath, "--nologo", "-c", configuration], output, cancellationToken);

    /// <summary>Restores the project's NuGet packages, which records their assemblies in <c>obj/project.assets.json</c>.</summary>
    public static Task<int> RestoreAsync(string csprojPath, Action<string> output, CancellationToken cancellationToken) =>
        RunToEndAsync(["restore", csprojPath], output, cancellationToken);

    private static async Task<int> RunToEndAsync(string[] arguments, Action<string> output, CancellationToken cancellationToken)
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

/// <summary>A running <c>dotnet</c> process whose output is forwarded line by line.</summary>
public sealed class DotNetProcess : IDisposable
{
    private readonly Process process;

    private DotNetProcess(Process process)
    {
        this.process = process;
    }

    public static DotNetProcess Start(IEnumerable<string> arguments, Action<string> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var process = new Process { StartInfo = DotNetCli.CreateStartInfo(arguments), EnableRaisingEvents = true };
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
            throw new InvalidOperationException("The .NET SDK ('dotnet') was not found. Install the .NET 10 SDK and make sure it is on PATH.", exception);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new DotNetProcess(process);
    }

    public bool HasExited => process.HasExited;

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
