using ScriptArt.Compiler;
using ScriptArt.Compiler.Diagnostics;
using ScriptArt.Compiler.Nodes;
using ScriptArt.GraphModel;
using ScriptArt.ProjectSystem;

namespace ScriptArt.Cli;

/// <summary>
/// <c>vside generate|diff|build|run [--node-library &lt;dll&gt;]... &lt;project.vside&gt;</c> and
/// <c>vside migrate [--check] &lt;project.vside&gt;</c>. Exit code 0 on success, 1 on compile or build errors (or, for
/// <c>migrate --check</c>, outdated files), 2 on usage errors. <c>diff</c> prints what <c>generate</c> would change in the generated
/// files as a unified diff, and writes nothing. A node library is loaded only when named with <c>--node-library</c>: it runs code
/// with the user's permissions, so naming it is the opt-in (D-038).
/// </summary>
internal static class Program
{
    private const string Usage = """
        Usage: vside <generate|diff|build|run> [--node-library <path/to/library.dll>]... <path/to/project.vside>
               vside migrate [--check] <path/to/project.vside>
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args is ["migrate", .. var migrateArgs])
        {
            return migrateArgs switch
            {
                ["--check", var path] => Migrate(path, check: true),
                [var path] when !path.StartsWith("--", StringComparison.Ordinal) => Migrate(path, check: false),
                _ => await UsageErrorAsync().ConfigureAwait(false),
            };
        }

        if (args is not [var command, .. var rest] || command is not ("generate" or "diff" or "build" or "run")
            || ParseOptions(rest) is not ({ } projectPath, var libraryPaths))
        {
            return await UsageErrorAsync().ConfigureAwait(false);
        }

        VisualProject project;
        try
        {
            project = VisualProject.Load(projectPath);
        }
        catch (Exception exception) when (exception is GraphFormatException or IOException or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 2;
        }

        // Package methods are only known after restore, which a fresh checkout has not run yet.
        if (project.References.PackagesNotRestored
            && await DotNetCli.RestoreAsync(project.CsprojPath, Console.WriteLine, CancellationToken.None).ConfigureAwait(false) != 0)
        {
            return 1;
        }

        NodeLibrary library = NodeLibrary.CreateDefault(project.References.AssemblyPaths);
        foreach (string libraryPath in libraryPaths)
        {
            NodeLibraryContents contents = NodeLibraryLoader.Load(libraryPath);
            foreach (string problem in contents.Problems.Concat(library.RegisterLibrary(contents)))
            {
                await Console.Error.WriteLineAsync(problem).ConfigureAwait(false);
            }

            if (contents.Definitions.Count == 0)
            {
                return 2;
            }
        }

        var compiler = new GraphCompiler(library);
        ProjectGenerationResult generation = command == "diff" ? project.PreviewGeneration(compiler) : project.Generate(compiler);
        foreach (GraphDiagnostic diagnostic in generation.Diagnostics)
        {
            await Console.Error.WriteLineAsync(Describe(project, diagnostic)).ConfigureAwait(false);
        }

        if (!generation.Success)
        {
            return 1;
        }

        if (command == "diff")
        {
            foreach (GeneratedFileChange change in generation.Changes)
            {
                Console.Write(change.UnifiedDiff(Path.GetRelativePath(project.DirectoryPath, change.Path).Replace('\\', '/')));
            }

            return 0;
        }

        foreach (string file in generation.ChangedFiles)
        {
            Console.WriteLine($"Updated {Path.GetRelativePath(project.DirectoryPath, file)}");
        }

        if (command == "generate")
        {
            return 0;
        }

        int buildExit = await DotNetCli.BuildAsync(project.CsprojPath, "Debug", Console.WriteLine, CancellationToken.None).ConfigureAwait(false);
        if (buildExit != 0 || command == "build")
        {
            return buildExit == 0 ? 0 : 1;
        }

        using DotNetProcess run = DotNetCli.StartRun(project.CsprojPath, "Debug", Console.WriteLine);
        Task<int> exited = run.WaitForExitAsync();
        while (!exited.IsCompleted && await Console.In.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            run.WriteInput(line);
        }

        run.CloseInput();
        return await exited.ConfigureAwait(false);
    }

    // The project path and any --node-library paths; null project path when the arguments are not valid.
    private static (string? ProjectPath, List<string> LibraryPaths) ParseOptions(string[] args)
    {
        List<string> libraries = [];
        string? projectPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--node-library" && i + 1 < args.Length)
            {
                libraries.Add(args[++i]);
            }
            else if (projectPath is null && !args[i].StartsWith("--", StringComparison.Ordinal))
            {
                projectPath = args[i];
            }
            else
            {
                return (null, libraries);
            }
        }

        return (projectPath, libraries);
    }

    private static async Task<int> UsageErrorAsync()
    {
        await Console.Error.WriteLineAsync(Usage).ConfigureAwait(false);
        return 2;
    }

    /// <summary>Lists the project's files that use an older schema and, unless <paramref name="check"/>, rewrites them.</summary>
    private static int Migrate(string projectPath, bool check)
    {
        IReadOnlyList<FileSchemaVersion> files;
        try
        {
            files = ProjectMigration.Inspect(projectPath);
        }
        catch (Exception exception) when (exception is GraphFormatException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        string directory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        FileSchemaVersion[] outdated = [.. files.Where(file => file.IsOutdated)];
        if (outdated.Length == 0)
        {
            Console.WriteLine($"All {files.Count} files use the current schema.");
            return 0;
        }

        foreach (FileSchemaVersion file in outdated)
        {
            Console.WriteLine($"{Path.GetRelativePath(directory, file.Path)}: schema {file.Version} -> {file.CurrentVersion}");
            if (file.Path.EndsWith(GraphSerializer.FileExtension, StringComparison.OrdinalIgnoreCase))
            {
                foreach (SchemaMigration step in ProjectMigration.GraphStepsFrom(file.Version))
                {
                    Console.WriteLine($"    {step.ToVersion}: {step.Description}");
                }
            }
        }

        if (check)
        {
            Console.WriteLine($"{outdated.Length} file(s) use an older schema. Run 'vside migrate {projectPath}' to upgrade them.");
            return 1;
        }

        try
        {
            ProjectMigration.Migrate(projectPath);
        }
        catch (Exception exception) when (exception is GraphFormatException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }

        Console.WriteLine($"Upgraded {outdated.Length} file(s); the originals are in MigrationBackup. Older versions of ScriptArt Code cannot open the upgraded files.");
        return 0;
    }

    private static string Describe(VisualProject project, GraphDiagnostic diagnostic)
    {
        string location = diagnostic switch
        {
            { FilePath: { } file, Line: { } line } => $"{Path.GetRelativePath(project.DirectoryPath, file)}({line})",
            { GraphId: { } graphId } => project.Graphs.FirstOrDefault(graph => graph.Graph.Id == graphId)?.RelativePath ?? graphId,
            _ => project.Name,
        };
        string node = diagnostic.NodeId is null ? "" : $" [node {diagnostic.NodeId}]";
        return $"{location}: {diagnostic.Severity.ToString().ToLowerInvariant()} {diagnostic.Id}: {diagnostic.Message}{node}";
    }
}
