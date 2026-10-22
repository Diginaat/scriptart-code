using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ScriptArt.Compiler;
using ScriptArt.Compiler.Emit;
using ScriptArt.Compiler.Nodes;
using ScriptArt.GraphModel;
using ScriptArt.ProjectSystem;

namespace ScriptArt.IntegrationTests;

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

    [Fact]
    public async Task TypesSampleSharesClassObjectsAndCopiesStructs()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = CopySample("Types", directory.Path);

        Assert.Equal("Ada has 100\nPoint 3, 4\n", await GenerateBuildAndRun(project, input: null));
    }

    [Fact]
    public async Task MethodsSampleChangesAnObjectAndComputesFromAValue()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = CopySample("Methods", directory.Path);

        Assert.Equal("Count 5\nLength squared 25\n", await GenerateBuildAndRun(project, input: null));
    }

    [Fact]
    public async Task DictionariesSampleCountsWordsIntoAListOfStructs()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = CopySample("Dictionaries", directory.Path);

        Assert.Equal("the: 2\ncat: 1\nand: 1\nhat: 1\n4 different words\n", await GenerateBuildAndRun(project, input: null));
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

    [Fact]
    public async Task CollectionsSampleParsesAListAndSumsIt()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = CopySample("Collections", directory.Path);

        Assert.Equal("Enter numbers separated by commas:\nCount: 3\nSum: 12\n", await GenerateBuildAndRun(project, "3,4,5"));
    }

    [Fact]
    public async Task MethodsOfAReferencedAssemblyAreCalledFromTheGraph()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "WithLibrary");
        string library = Path.Combine(directory.Path, "lib", "Greetings.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(library)!);
        CSharpCompilation compilation = CSharpCompilation.Create("Greetings",
            [CSharpSyntaxTree.ParseText("namespace Greetings; public static class Greeter { public static string Greet(string name) => \"Hi \" + name; }",
                cancellationToken: TestContext.Current.CancellationToken)],
            [MetadataReference.CreateFromFile(ReferencePackAssembly("System.Runtime.dll"))],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.True(compilation.Emit(library, cancellationToken: TestContext.Current.CancellationToken).Success);
        await File.WriteAllTextAsync(project.CsprojPath, VisualProject.CsprojTemplate(project.Document).Replace("</Project>",
            "  <ItemGroup>\n    <Reference Include=\"Greetings\"><HintPath>lib\\Greetings.dll</HintPath></Reference>\n  </ItemGroup>\n\n</Project>",
            StringComparison.Ordinal), TestContext.Current.CancellationToken);
        PrintResultOfCall(project, "M:Greetings.Greeter.Greet(System.String)", "name", "Ada");

        var compiler = new GraphCompiler(NodeLibrary.CreateDefault(project.References.AssemblyPaths));

        Assert.Equal("Hi Ada\n", await GenerateBuildAndRun(project, input: null, compiler));
    }

    [Fact]
    public async Task MethodsOfANuGetPackageAreCalledAfterRestore()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "WithPackage");
        await File.WriteAllTextAsync(project.CsprojPath, VisualProject.CsprojTemplate(project.Document).Replace("</Project>",
            "  <ItemGroup>\n    <PackageReference Include=\"Humanizer.Core\" Version=\"2.14.1\" />\n  </ItemGroup>\n\n</Project>",
            StringComparison.Ordinal), TestContext.Current.CancellationToken);
        Assert.True(project.References.PackagesNotRestored);
        var log = new StringBuilder();
        int restore = await DotNetCli.RestoreAsync(project.CsprojPath, line => { lock (log) { log.AppendLine(line); } }, TestContext.Current.CancellationToken);
        Assert.SkipWhen(restore != 0, $"NuGet restore failed, probably because there is no network access:\n{log}");
        PrintResultOfCall(project, "M:Humanizer.StringHumanizeExtensions.Humanize(System.String)", "input", "some_text_here");

        var compiler = new GraphCompiler(NodeLibrary.CreateDefault(project.References.AssemblyPaths));

        Assert.Equal("some text here\n", await GenerateBuildAndRun(project, input: null, compiler));
    }

    [Theory]
    [InlineData("HelloWorld")]
    [InlineData("ConsoleApplication")]
    [InlineData("Variables")]
    [InlineData("Loops")]
    [InlineData("Functions")]
    [InlineData("Exceptions")]
    [InlineData("Collections")]
    [InlineData("Types")]
    [InlineData("Dictionaries")]
    [InlineData("Methods")]
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

    /// <summary>
    /// Replaces the project's entry graph with Entry, then a call to the .NET method <paramref name="documentationId"/> with the
    /// string <paramref name="argument"/>, then Print Line of its result.
    /// </summary>
    private static void PrintResultOfCall(VisualProject project, string documentationId, string parameter, string argument)
    {
        GraphDocument graph = project.Graphs[0].Graph;
        graph.Nodes.Clear();
        graph.Connections.Clear();
        GraphNode Add(string type, SortedDictionary<string, string>? properties = null)
        {
            var node = new GraphNode { Id = GraphDocument.NewId(), Type = type, Properties = properties ?? new(StringComparer.Ordinal) };
            graph.Nodes.Add(node);
            return node;
        }

        GraphNode entry = Add(BuiltInNodes.EntryTypeId);
        GraphNode call = Add(ExternalApi.TypeIdPrefix + documentationId);
        GraphNode text = Add("literal.string", new(StringComparer.Ordinal) { ["value"] = argument });
        GraphNode print = Add("console.write-line");
        graph.Connections.AddRange([
            new(new PinRef(entry.Id, "then"), new PinRef(call.Id, "exec")),
            new(new PinRef(text.Id, "value"), new PinRef(call.Id, parameter)),
            new(new PinRef(call.Id, "then"), new PinRef(print.Id, "exec")),
            new(new PinRef(call.Id, "result"), new PinRef(print.Id, "text")),
        ]);
    }

    /// <summary>
    /// An assembly of the installed net10.0 reference pack. A library compiled against the runtime's implementation assemblies
    /// would reference System.Private.CoreLib, which a project build cannot resolve.
    /// </summary>
    private static string ReferencePackAssembly(string fileName)
    {
        // The runtime lives in <dotnet>/shared/Microsoft.NETCore.App/<version>/; reference packs in <dotnet>/packs/.
        string dotnet = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", "..", ".."));
        string pack = Directory.GetDirectories(Path.Combine(dotnet, "packs", "Microsoft.NETCore.App.Ref"))
            .Where(directory => File.Exists(Path.Combine(directory, "ref", "net10.0", fileName)))
            .Order(StringComparer.Ordinal)
            .Last();
        return Path.Combine(pack, "ref", "net10.0", fileName);
    }

    private static Task<string> GenerateBuildAndRun(VisualProject project, string? input) => GenerateBuildAndRun(project, input, Compiler);

    private static async Task<string> GenerateBuildAndRun(VisualProject project, string? input, GraphCompiler compiler)
    {
        ProjectGenerationResult generation = project.Generate(compiler);
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
            if (File.Exists(Path.Combine(directory.FullName, "ScriptArtCode.slnx")) && Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository's samples directory.");
    }

    /// <summary>Copies a sample's project file and graphs (not its generated code) into <paramref name="destination"/> and loads it.</summary>
    internal static VisualProject CopySample(string sample, string destination)
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
