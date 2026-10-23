using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using ScriptArt.Compiler.Emit;

namespace ScriptArt.ProjectSystem.Debugging;

/// <summary>A stack frame. <paramref name="Path"/> is null for code without source, such as the framework.</summary>
public sealed record DebugFrame(int Id, string Name, string? Path, int Line);

/// <summary>A variable; <paramref name="ChildrenReference"/> is non-zero when it has members to expand.</summary>
public sealed record DebugVariable(string Name, string Value, string? Type, int ChildrenReference);

/// <summary>Why and where the program stopped. <paramref name="Frame"/> is the innermost frame, when the debugger reports one.</summary>
public sealed record DebugStop(string Reason, int ThreadId, DebugFrame? Frame, string? Description);

/// <summary>Finds the debugger the IDE drives: netcoredbg, the MIT-licensed .NET debugger with a Debug Adapter Protocol interface.</summary>
public static class DebuggerLocator
{
    /// <summary>An environment variable with the full path of <c>netcoredbg</c>; it takes precedence over the search.</summary>
    public const string PathVariable = "VSIDE_NETCOREDBG";

    /// <summary>
    /// The path of <c>netcoredbg</c>: <see cref="PathVariable"/>, then <paramref name="preferredPath"/> (the path set in
    /// preferences) when it exists, then <c>netcoredbg</c> on <c>PATH</c>, then <c>netcoredbg/netcoredbg</c> in the IDE's folder under
    /// the local application data folder; null when none exists.
    /// </summary>
    public static string? Find(string? preferredPath = null)
    {
        if (Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } configured)
        {
            return File.Exists(configured) ? Path.GetFullPath(configured) : null;
        }

        if (preferredPath is { Length: > 0 } && File.Exists(preferredPath))
        {
            return Path.GetFullPath(preferredPath);
        }

        string executable = OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg";
        IEnumerable<string> folders = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScriptArtCode", "netcoredbg"));
        foreach (string folder in folders)
        {
            try
            {
                string candidate = Path.Combine(folder, executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A PATH entry with characters a path cannot have.
            }
        }

        return null;
    }
}

/// <summary>
/// Debugs a built program with netcoredbg over the Debug Adapter Protocol. The IDE starts the program itself, with its input and
/// output connected like <see cref="DotNetCli.StartRun"/>; a startup hook (<see cref="DebugStartupHook"/>) holds it before
/// <c>Main</c> until the debugger has attached and set the breakpoints. Events are raised on a background thread.
/// </summary>
public sealed class DebugSession : IAsyncDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private readonly DotNetProcess program;
    private readonly Process adapter;
    private readonly DebugAdapterConnection connection;
    private int lastThreadId;

    private DebugSession(DotNetProcess program, Process adapter, DebugAdapterConnection connection)
    {
        this.program = program;
        this.adapter = adapter;
        this.connection = connection;
    }

    /// <summary>The program stopped at a breakpoint, after a step, on a pause, or on an exception.</summary>
    public event Action<DebugStop>? Stopped;

    /// <summary>The program runs again after a stop.</summary>
    public event Action? Continued;

    /// <summary>
    /// Starts <paramref name="programPath"/> (a built <c>.dll</c>) under <c>dotnet</c> and attaches <paramref name="debuggerPath"/>
    /// with <paramref name="breakpoints"/> (lines by file) set. Program output and debugger messages go to <paramref name="output"/>.
    /// </summary>
    public static async Task<DebugSession> StartAsync(string debuggerPath, string programPath, IReadOnlyDictionary<string, IReadOnlyList<int>> breakpoints,
        Action<string> output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(breakpoints);
        ArgumentNullException.ThrowIfNull(output);
        if (!File.Exists(programPath))
        {
            throw new InvalidOperationException($"'{programPath}' does not exist. Build the project first.");
        }

        string temporary = Path.Combine(Path.GetTempPath(), "ScriptArtCode", "debug");
        string hook = DebugStartupHook.EnsureBuilt(temporary);
        string readyFile = Path.Combine(temporary, "ready-" + Guid.NewGuid().ToString("N"));
        DotNetProcess program = DotNetProcess.Start([programPath], output, new Dictionary<string, string>
        {
            ["DOTNET_STARTUP_HOOKS"] = hook,
            [DebugStartupHook.ReadyFileVariable] = readyFile,
            [DebugStartupHook.TimeoutVariable] = StartTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture),
        });

        Process? adapter = null;
        try
        {
            // The hook creates the file once the runtime is up, which is when the debugger can attach.
            var waited = Stopwatch.StartNew();
            while (!File.Exists(readyFile))
            {
                if (program.HasExited || waited.Elapsed > StartTimeout)
                {
                    throw new InvalidOperationException("The program did not start for debugging.");
                }

                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }

            File.Delete(readyFile);
            adapter = StartAdapter(debuggerPath, output);
            var session = new DebugSession(program, adapter, new DebugAdapterConnection(adapter.StandardOutput.BaseStream, adapter.StandardInput.BaseStream));
            await session.AttachAsync(breakpoints, cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            program.Stop();
            program.Dispose();
            if (adapter is not null)
            {
                Kill(adapter);
                adapter.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// Starts the Godot project in <paramref name="projectDirectory"/> with <paramref name="godotPath"/>
    /// (<see cref="DotNetProcess.StartGodot"/>) and attaches <paramref name="debuggerPath"/> once Godot has loaded the .NET runtime
    /// (D-047). Godot hosts the runtime itself, so no startup hook holds the game: breakpoints are set a moment after it starts, and
    /// code that runs before then, such as the first scene's <c>_Ready</c>, may run without stopping.
    /// </summary>
    public static async Task<DebugSession> StartGodotAsync(string debuggerPath, string godotPath, string projectDirectory,
        IReadOnlyDictionary<string, IReadOnlyList<int>> breakpoints, Action<string> output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(breakpoints);
        ArgumentNullException.ThrowIfNull(output);
        DotNetProcess game = DotNetProcess.StartGodot(godotPath, projectDirectory, output);
        Process? adapter = null;
        try
        {
            var waited = Stopwatch.StartNew();
            while (!HasLoadedRuntime(game.Id))
            {
                if (game.HasExited || waited.Elapsed > StartTimeout)
                {
                    throw new InvalidOperationException("Godot did not load the .NET runtime. Is it the .NET (mono) build of Godot 4, and is the project built?");
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }

            adapter = StartAdapter(debuggerPath, output);
            var session = new DebugSession(game, adapter, new DebugAdapterConnection(adapter.StandardOutput.BaseStream, adapter.StandardInput.BaseStream));
            await session.AttachAsync(breakpoints, cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            game.Stop();
            game.Dispose();
            if (adapter is not null)
            {
                Kill(adapter);
                adapter.Dispose();
            }

            throw;
        }
    }

    // True once the process has loaded CoreCLR (coreclr.dll, libcoreclr.so, or libcoreclr.dylib), which the debugger attaches to.
    private static bool HasLoadedRuntime(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.Modules.Cast<ProcessModule>().Any(module =>
                Path.GetFileNameWithoutExtension(module.ModuleName).Equals(OperatingSystem.IsWindows() ? "coreclr" : "libcoreclr", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Not started yet, already gone, or its modules cannot be read yet.
            return false;
        }
    }

    private static Process StartAdapter(string debuggerPath, Action<string> output)
    {
        var startInfo = new ProcessStartInfo(debuggerPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--interpreter=vscode");
        var adapter = new Process { StartInfo = startInfo };
        adapter.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { Length: > 0 } line)
            {
                output("[debugger] " + line);
            }
        };

        try
        {
            adapter.Start();
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            adapter.Dispose();
            throw new InvalidOperationException($"The debugger '{debuggerPath}' could not be started: {exception.Message}", exception);
        }

        adapter.BeginErrorReadLine();
        return adapter;
    }

    private async Task AttachAsync(IReadOnlyDictionary<string, IReadOnlyList<int>> breakpoints, CancellationToken cancellationToken)
    {
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.EventReceived += (name, body) =>
        {
            switch (name)
            {
                case "initialized":
                    initialized.TrySetResult();
                    break;
                case "stopped":
                    _ = ReportStopAsync(body);
                    break;
                case "continued":
                    Continued?.Invoke();
                    break;
            }
        };
        connection.Start();

        await connection.RequestAsync("initialize", new JsonObject
        {
            ["clientID"] = "vside",
            ["clientName"] = "ScriptArt Code",
            ["adapterID"] = "coreclr",
            ["linesStartAt1"] = true,
            ["columnsStartAt1"] = true,
            ["pathFormat"] = "path",
        }, cancellationToken).ConfigureAwait(false);
        await connection.RequestAsync("attach", new JsonObject
        {
            ["name"] = "ScriptArt Code",
            ["type"] = "coreclr",
            ["request"] = "attach",
            ["processId"] = program.Id,
        }, cancellationToken).ConfigureAwait(false);
        await initialized.Task.WaitAsync(StartTimeout, cancellationToken).ConfigureAwait(false);
        foreach ((string path, IReadOnlyList<int> lines) in breakpoints)
        {
            await SetBreakpointsAsync(path, lines, cancellationToken).ConfigureAwait(false);
        }

        await connection.RequestAsync("configurationDone", null, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReportStopAsync(JsonNode? body)
    {
        int threadId = body?["threadId"]?.GetValue<int>() ?? lastThreadId;
        lastThreadId = threadId;
        DebugFrame? frame = null;
        try
        {
            IReadOnlyList<DebugFrame> frames = await StackTraceAsync(threadId, levels: 1).ConfigureAwait(false);
            frame = frames.Count > 0 ? frames[0] : null;
        }
        catch (DebugAdapterException)
        {
            // The program went on or ended before its stack could be read.
        }

        Stopped?.Invoke(new DebugStop((string?)body?["reason"] ?? "pause", threadId, frame, (string?)body?["text"] ?? (string?)body?["description"]));
    }

    /// <summary>Exits when the program exits, with its exit code.</summary>
    public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) => program.WaitForExitAsync(cancellationToken);

    /// <summary>Sends one line to the program's standard input.</summary>
    public void WriteInput(string line) => program.WriteInput(line);

    /// <summary>Replaces the breakpoints of the file <paramref name="path"/>; an empty list removes them.</summary>
    public Task SetBreakpointsAsync(string path, IReadOnlyList<int> lines, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return connection.RequestAsync("setBreakpoints", new JsonObject
        {
            ["source"] = new JsonObject { ["path"] = path },
            ["breakpoints"] = new JsonArray([.. lines.Select(line => (JsonNode)new JsonObject { ["line"] = line })]),
        }, cancellationToken);
    }

    public Task ContinueAsync() => connection.RequestAsync("continue", new JsonObject { ["threadId"] = lastThreadId });

    public Task StepOverAsync() => connection.RequestAsync("next", new JsonObject { ["threadId"] = lastThreadId });

    public Task StepIntoAsync() => connection.RequestAsync("stepIn", new JsonObject { ["threadId"] = lastThreadId });

    public Task StepOutAsync() => connection.RequestAsync("stepOut", new JsonObject { ["threadId"] = lastThreadId });

    /// <summary>Stops the running program where it is.</summary>
    public async Task PauseAsync()
    {
        JsonNode? threads = await connection.RequestAsync("threads", null).ConfigureAwait(false);
        int threadId = threads?["threads"]?.AsArray().FirstOrDefault()?["id"]?.GetValue<int>() ?? lastThreadId;
        await connection.RequestAsync("pause", new JsonObject { ["threadId"] = threadId }).ConfigureAwait(false);
    }

    /// <summary>The frames of <paramref name="threadId"/>'s stack, innermost first.</summary>
    public async Task<IReadOnlyList<DebugFrame>> StackTraceAsync(int threadId, int levels = 50)
    {
        JsonNode? body = await connection.RequestAsync("stackTrace", new JsonObject { ["threadId"] = threadId, ["levels"] = levels }).ConfigureAwait(false);
        return [.. (body?["stackFrames"]?.AsArray() ?? []).OfType<JsonNode>().Select(frame => new DebugFrame(
            frame["id"]?.GetValue<int>() ?? 0,
            (string?)frame["name"] ?? "",
            (string?)frame["source"]?["path"],
            frame["line"]?.GetValue<int>() ?? 0))];
    }

    /// <summary>The local variables and parameters of a frame.</summary>
    public async Task<IReadOnlyList<DebugVariable>> LocalsAsync(int frameId)
    {
        JsonNode? body = await connection.RequestAsync("scopes", new JsonObject { ["frameId"] = frameId }).ConfigureAwait(false);
        int reference = (body?["scopes"]?.AsArray().FirstOrDefault())?["variablesReference"]?.GetValue<int>() ?? 0;
        return reference == 0 ? [] : await ChildrenAsync(reference).ConfigureAwait(false);
    }

    /// <summary>The members of a variable whose <see cref="DebugVariable.ChildrenReference"/> is <paramref name="reference"/>.</summary>
    public async Task<IReadOnlyList<DebugVariable>> ChildrenAsync(int reference)
    {
        JsonNode? body = await connection.RequestAsync("variables", new JsonObject { ["variablesReference"] = reference }).ConfigureAwait(false);
        return [.. (body?["variables"]?.AsArray() ?? []).OfType<JsonNode>().Select(variable => new DebugVariable(
            (string?)variable["name"] ?? "",
            (string?)variable["value"] ?? "",
            (string?)variable["type"],
            variable["variablesReference"]?.GetValue<int>() ?? 0))];
    }

    /// <summary>Ends the program and the debugger.</summary>
    public async ValueTask DisposeAsync()
    {
        program.Stop();
        try
        {
            await connection.RequestAsync("disconnect", new JsonObject { ["terminateDebuggee"] = true })
                .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DebugAdapterException or TimeoutException)
        {
            // The debugger already ended with the program.
        }

        Kill(adapter);
        try
        {
            await connection.Completion.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The reader ends with the killed adapter's output; nothing waits for it.
        }

        connection.Dispose();
        adapter.Dispose();
        program.Dispose();
    }

    private static void Kill(Process process)
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
            // It exited between the check and the kill.
        }
    }
}
