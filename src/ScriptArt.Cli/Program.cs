using ScriptArt.Compiler;
using ScriptArt.Compiler.Diagnostics;
using ScriptArt.Compiler.Nodes;
using ScriptArt.GraphModel;
using ScriptArt.ProjectSystem;

namespace ScriptArt.Cli;

/// <summary>
/// <c>vside generate|diff|build|run &lt;project.vside&gt;</c>. Exit code 0 on success, 1 on compile or build errors, 2 on usage errors.
/// <c>diff</c> prints what <c>generate</c> would change in the generated files as a unified diff, and writes nothing.
/// </summary>
internal static class Program
{
    private const string Usage = "Usage: vside <generate|diff|build|run> <path/to/project.vside>";

    public static async Task<int> Main(string[] args)
    {
        if (args is not [var command, var projectPath] || command is not ("generate" or "diff" or "build" or "run"))
        {
            await Console.Error.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
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

        var compiler = new GraphCompiler(NodeLibrary.CreateDefault(project.References.AssemblyPaths));
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
