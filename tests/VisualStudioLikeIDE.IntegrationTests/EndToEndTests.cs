using System.Text;
using VisualStudioLikeIDE.Compiler;
using VisualStudioLikeIDE.Compiler.Emit;
using VisualStudioLikeIDE.Compiler.Nodes;
using VisualStudioLikeIDE.ProjectSystem;

namespace VisualStudioLikeIDE.IntegrationTests;

/// <summary>
/// Generates projects, builds them with the installed .NET SDK, runs them, and checks what they print.
/// These tests launch real processes and take several seconds each.
/// </summary>
public sealed class EndToEndTests
{
    private static readonly GraphCompiler Compiler = new(NodeLibrary.CreateDefault());

    [Fact]
    public async Task NewProjectBuildsAndPrintsGreeting()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "Greeting");

        string output = await GenerateBuildAndRun(project, input: null);

        Assert.Equal("Hello, World!\n", output);
    }

    [Theory]
    [InlineData("Ada", "What is your name?\nHello, Ada!\nGoodbye.\n")]
    [InlineData("", "What is your name?\nYou did not enter a name.\n")]
    public async Task ConsoleApplicationSampleBehavesAsDesigned(string input, string expected)
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = CopySample("ConsoleApplication", directory.Path);

        Assert.Equal(expected, await GenerateBuildAndRun(project, input));
    }

    [Fact]
    public async Task VariablesSampleKeepsValuesBetweenStatements()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = CopySample("Variables", directory.Path);

        Assert.Equal("What is your name?\nHello, Ada\nGreetings so far: 1\n", await GenerateBuildAndRun(project, "Ada"));
    }

    [Theory]
    [InlineData("a\nb\n", "You entered 2 lines.")]
    [InlineData("", "You entered 0 lines.")]
    public async Task LoopsSampleCountsAndStopsAtAnEmptyLine(string input, string report)
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = CopySample("Loops", directory.Path);

        Assert.Equal($"Line 1\nLine 2\nLine 3\nType lines, then an empty line:\n{report}\n", await GenerateBuildAndRun(project, input));
    }

    [Fact]
    public async Task FunctionsSampleCallsVoidAndRecursiveFunctions()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = CopySample("Functions", directory.Path);

        Assert.Equal("Hello, functions!\n1! = 1\n2! = 2\n3! = 6\n4! = 24\n5! = 120\n", await GenerateBuildAndRun(project, input: null));
    }

    [Theory]
    [InlineData("21", "42\n")]
    [InlineData("abc", "Not a number: The input string 'abc' was not in a correct format.\n")]
    public async Task ExceptionsSampleCatchesParseErrorsAndAlwaysFinishes(string input, string result)
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = CopySample("Exceptions", directory.Path);

        Assert.Equal($"Enter a number:\n{result}Done.\n", await GenerateBuildAndRun(project, input));
    }

    [Theory]
    [InlineData("HelloWorld")]
    [InlineData("ConsoleApplication")]
    [InlineData("Variables")]
    [InlineData("Loops")]
    [InlineData("Functions")]
    [InlineData("Exceptions")]
    public void CheckedInSampleCodeMatchesGenerator(string sample)
    {
        // The samples directory holds the generated code so samples build without the IDE. It must stay current.
        using var directory = new TemporaryDirectory();
        VisualProject project = CopySample(sample, directory.Path);

        ProjectGenerationResult result = project.Generate(Compiler);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        foreach (GeneratedSource source in result.Sources)
        {
            string checkedIn = Path.Combine(RepositorySamples(), sample, "Generated", source.RelativePath);
            Assert.Equal(File.ReadAllText(checkedIn).ReplaceLineEndings("\n"), source.Text);
        }
    }

    [Fact]
    public async Task HandwrittenCodeIsCompiledWithGeneratedCode()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "Mixed");
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "Helpers.cs"),
            "namespace Mixed;\n\ninternal static class Helpers\n{\n    public static int Answer => 42;\n}\n", TestContext.Current.CancellationToken);

        Assert.Equal("Hello, World!\n", await GenerateBuildAndRun(project, input: null));
        Assert.True(File.Exists(Path.Combine(directory.Path, "Helpers.cs")));
    }

    private static async Task<string> GenerateBuildAndRun(VisualProject project, string? input)
    {
        ProjectGenerationResult generation = project.Generate(Compiler);
        Assert.True(generation.Success, string.Join("\n", generation.Diagnostics));

        var buildLog = new StringBuilder();
        int buildExit = await DotNetCli.BuildAsync(project.CsprojPath, "Debug", line => { lock (buildLog) { buildLog.AppendLine(line); } }, TestContext.Current.CancellationToken);
        Assert.True(buildExit == 0, buildLog.ToString());

        var output = new StringBuilder();
        using DotNetProcess run = DotNetCli.StartRun(project.CsprojPath, "Debug", line => { lock (output) { output.Append(line).Append('\n'); } });
        if (input is not null)
        {
            run.WriteInput(input);
        }

        run.CloseInput();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        int exit = await run.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, exit);
        lock (output)
        {
            return output.ToString();
        }
    }

    private static string RepositorySamples()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "samples");
            if (File.Exists(Path.Combine(directory.FullName, "VisualStudioLikeIDE.slnx")) && Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository's samples directory.");
    }

    /// <summary>Copies a sample's project file and graphs (not its generated code) into <paramref name="destination"/> and loads it.</summary>
    private static VisualProject CopySample(string sample, string destination)
    {
        string source = Path.Combine(AppContext.BaseDirectory, "samples", sample);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return VisualProject.Load(Path.Combine(destination, sample + VisualProject.FileExtension));
    }
}
