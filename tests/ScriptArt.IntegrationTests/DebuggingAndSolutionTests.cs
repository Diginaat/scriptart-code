using System.Text;
using ScriptArt.Compiler;
using ScriptArt.Compiler.Nodes;
using ScriptArt.GraphModel;
using ScriptArt.ProjectSystem;
using ScriptArt.ProjectSystem.Debugging;

namespace ScriptArt.IntegrationTests;

/// <summary>
/// Debugging a sample with netcoredbg (skipped when it is not installed; see <see cref="DebuggerLocator"/>), and solution and project
/// reference management through the real <c>dotnet</c> commands.
/// </summary>
public sealed class DebuggingAndSolutionTests
{
    private static readonly GraphCompiler Compiler = new(NodeLibrary.CreateDefault());

    [Fact]
    public async Task NodeBreakpointStopsTheProgramThatStillReadsInput()
    {
        if (DebuggerLocator.Find() is not { } debugger)
        {
            Assert.Skip($"netcoredbg is not installed. Put it on PATH or set {DebuggerLocator.PathVariable} to run this test.");
            return;
        }

        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        VisualProject project = EndToEndTests.CopySample("Variables", directory.Path);
        ProjectGenerationResult generation = project.Generate(Compiler);
        Assert.True(generation.Success, string.Join("\n", generation.Diagnostics));
        Assert.Equal(0, await DotNetCli.BuildAsync(project.CsprojPath, "Debug", _ => { }, cancellation));

        // "store-name" sets the variable name to the line just read.
        var map = new DebugSourceMap(project, generation.Sources);
        ProjectGraph program = project.Graphs[0];
        var output = new StringBuilder();
        var stops = new System.Collections.Concurrent.BlockingCollection<DebugStop>();
        await using DebugSession session = await DebugSession.StartAsync(debugger, project.OutputAssemblyPath("Debug"),
            map.Lines([new NodeBreakpoint(program.Graph.Id, "store-name")]), line => { lock (output) { output.Append(line).Append('\n'); } }, cancellation);
        session.Stopped += stops.Add;

        session.WriteInput("Ada");
        Assert.True(stops.TryTake(out DebugStop? stop, 30_000, cancellation), "The program did not stop at the breakpoint.");
        Assert.NotNull(stop);
        Assert.Equal("breakpoint", stop.Reason);
        Assert.NotNull(stop.Frame);
        Assert.Equal((program, "store-name"), map.NodeAt(stop.Frame.Path!, stop.Frame.Line));
        IReadOnlyList<DebugVariable> locals = await session.LocalsAsync(stop.Frame.Id);
        Assert.Contains(locals, variable => variable.Name == "line" && variable.Value == "\"Ada\"");

        await session.ContinueAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(1));
        Assert.Equal(0, await session.WaitForExitAsync(timeout.Token));
        lock (output)
        {
            Assert.Contains("Hello, Ada\n", output.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SolutionAndProjectReferenceMakeALibraryCallable()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(Path.Combine(directory.Path, "App"), "App");
        string library = Path.Combine(directory.Path, "Shapes", "Shapes.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(library)!);
        await File.WriteAllTextAsync(library, "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n", cancellation);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "Shapes", "Area.cs"),
            "namespace Shapes;\n\npublic static class Area\n{\n    public static string Describe(string name) => \"A \" + name;\n}\n", cancellation);
        var log = new StringBuilder();
        void Log(string line)
        {
            lock (log)
            {
                log.AppendLine(line);
            }
        }

        Assert.True(await ProjectSolution.CreateAsync(project, Log, cancellation) == 0, log.ToString());
        Assert.True(await ProjectSolution.AddProjectReferenceAsync(project, library, Log, cancellation) == 0, log.ToString());

        string solution = Assert.IsType<string>(ProjectSolution.Find(project));
        string solutionText = await File.ReadAllTextAsync(solution, cancellation);
        Assert.Contains("App.csproj", solutionText, StringComparison.Ordinal);
        Assert.Contains("Shapes.csproj", solutionText, StringComparison.Ordinal);
        Assert.Equal([library], project.References.Projects);

        Assert.True(project.Generate(Compiler).Success);
        Assert.True(await DotNetCli.BuildAsync(project.CsprojPath, "Debug", Log, cancellation) == 0, log.ToString());
        IReadOnlyList<string> assemblies = project.References.AssemblyPaths;
        Assert.Contains(assemblies, path => Path.GetFileName(path) == "Shapes.dll");
        Assert.Contains(ExternalApi.FromAssemblies(assemblies).Members, member => member.DisplayName.Contains("Describe", StringComparison.Ordinal));

        Assert.True(await ProjectSolution.RemoveProjectReferenceAsync(project, library, Log, cancellation) == 0, log.ToString());
        Assert.Empty(project.References.Projects);
    }
}
