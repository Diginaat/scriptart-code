using System.Text;
using ScriptArt.Compiler;
using ScriptArt.Compiler.Diagnostics;
using ScriptArt.Compiler.Emit;
using ScriptArt.Compiler.Nodes;
using ScriptArt.GraphModel;
using ScriptArt.NodeEditor;
using ScriptArt.ProjectSystem;

namespace ScriptArt.IntegrationTests;

/// <summary>
/// A new Godot 4 C# project restored with the real Godot.NET.Sdk and GodotSharp, generated, and built with the .NET SDK (Phase 7,
/// D-047). Skipped when the packages cannot be restored, usually without network access. Running the game needs the Godot editor
/// and is not tested here.
/// </summary>
public sealed class GodotProjectTests
{
    [Fact]
    public async Task ANewGodotProjectGeneratesAScriptThatGodotRecognizes()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.CreateGodot(directory.Path, "Hello");
        Assert.True(project.IsGodotProject);
        Assert.True(ProjectReferences.NeedsRestore(project.CsprojPath));
        var log = new StringBuilder();
        void Log(string line)
        {
            lock (log)
            {
                log.AppendLine(line);
            }
        }

        int restore = await DotNetCli.RestoreAsync(project.CsprojPath, Log, TestContext.Current.CancellationToken);
        Assert.SkipWhen(restore != 0, $"Restoring Godot.NET.Sdk failed, probably because there is no network access:\n{log}");

        Assert.False(ProjectReferences.NeedsRestore(project.CsprojPath));
        Assert.Contains(project.References.AssemblyPaths, path => Path.GetFileName(path) == "GodotSharp.dll");

        // GodotSharp's own API: Node._Ready is overridden and GD.Print(string) is a node.
        var compiler = new GraphCompiler(NodeLibrary.CreateDefault(project.References.AssemblyPaths));
        ProjectGenerationResult result = project.Generate(compiler);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        string main = await File.ReadAllTextAsync(Path.Combine(project.GeneratedDirectory, "Main.cs"), TestContext.Current.CancellationToken);
        Assert.Contains("public partial class Main : Node", main, StringComparison.Ordinal);
        Assert.Contains("GD.Print(\"Hello from ScriptArt Code!\");",
            await File.ReadAllTextAsync(Path.Combine(project.GeneratedDirectory, "Main._Ready.g.cs"), TestContext.Current.CancellationToken), StringComparison.Ordinal);

        // Godot's source generators attach a script path only to a class in the file named after it, which is how a scene finds it.
        int build = await DotNetCli.RunAsync(["build", project.CsprojPath, "-p:EmitCompilerGeneratedFiles=true", "--nologo"], Log, TestContext.Current.CancellationToken);
        Assert.True(build == 0, log.ToString());
        Assert.Contains(Directory.EnumerateFiles(Path.Combine(directory.Path, ".godot", "mono", "temp", "obj"), "*.cs", SearchOption.AllDirectories),
            file => File.ReadAllText(file).Contains("ScriptPathAttribute(\"res://Generated/Main.cs\")", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheGodotSampleMatchesTheGeneratorAndBuilds()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = EndToEndTests.CopySample("GodotMover", directory.Path);
        var log = new StringBuilder();
        void Log(string line)
        {
            lock (log)
            {
                log.AppendLine(line);
            }
        }

        int restore = await DotNetCli.RestoreAsync(project.CsprojPath, Log, TestContext.Current.CancellationToken);
        Assert.SkipWhen(restore != 0, $"Restoring Godot.NET.Sdk failed, probably because there is no network access:\n{log}");

        // Like CheckedInSampleCodeMatchesGenerator, which cannot cover this sample: its nodes come from GodotSharp, known after restore.
        ProjectGenerationResult result = project.Generate(new GraphCompiler(NodeLibrary.CreateDefault(project.References.AssemblyPaths)));

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        foreach (Compiler.Emit.GeneratedSource source in result.Sources)
        {
            string checkedIn = Path.Combine(EndToEndTests.RepositorySamples(), "GodotMover", "Generated", source.RelativePath);
            Assert.Equal((await File.ReadAllTextAsync(checkedIn, TestContext.Current.CancellationToken)).ReplaceLineEndings("\n"), source.Text);
        }

        Assert.True(await DotNetCli.BuildAsync(project.CsprojPath, "Debug", Log, TestContext.Current.CancellationToken) == 0, log.ToString());
    }

    // Prints from the generated Player once the game has run for a few frames, so the test sees that Godot loaded the script.
    private const string Probe = """
        using Godot;

        namespace GodotMover;

        public partial class Player
        {
            private int frames;

            public override void _Process(double delta)
            {
                if (++frames == 30)
                {
                    GD.Print($"probe: speed {Speed}");
                }
            }
        }
        """;

    [Fact]
    public async Task TheGodotSampleRunsInGodot()
    {
        // The Godot 4 .NET executable is not part of the SDK; set SCRIPTART_GODOT to its path to run this test.
        string? godot = Environment.GetEnvironmentVariable("SCRIPTART_GODOT");
        Assert.SkipWhen(string.IsNullOrEmpty(godot) || !File.Exists(godot), "SCRIPTART_GODOT does not name a Godot 4 .NET executable.");

        using var directory = new TemporaryDirectory();
        VisualProject project = EndToEndTests.CopySample("GodotMover", directory.Path);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "Probe.cs"), Probe, TestContext.Current.CancellationToken);
        var log = new StringBuilder();
        void Log(string line)
        {
            lock (log)
            {
                log.AppendLine(line);
            }
        }

        int restore = await DotNetCli.RestoreAsync(project.CsprojPath, Log, TestContext.Current.CancellationToken);
        Assert.SkipWhen(restore != 0, $"Restoring Godot.NET.Sdk failed, probably because there is no network access:\n{log}");
        Assert.True(project.Generate(new GraphCompiler(NodeLibrary.CreateDefault(project.References.AssemblyPaths))).Success);
        Assert.True(await DotNetCli.BuildAsync(project.CsprojPath, "Debug", Log, TestContext.Current.CancellationToken) == 0, log.ToString());

        using DotNetProcess game = DotNetProcess.StartGodot(godot!, project.DirectoryPath, Log, ["--headless", "--quit-after", "120"]);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await game.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            game.Stop();
        }

        // Speed is exported, so Godot also reads its default value; 300 shows the property kept it.
        Assert.Contains("probe: speed 300", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApprovedGodotSourceGeneratorsRunWhenTheCodeIsChecked()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = EndToEndTests.CopySample("GodotMover", directory.Path);

        // PropertyName exists only once Godot's ScriptPropertiesGenerator has run.
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "Names.cs"), """
            namespace GodotMover;

            public static class Names
            {
                public static Godot.StringName Speed => Player.PropertyName.Speed;
            }
            """, TestContext.Current.CancellationToken);
        var log = new StringBuilder();
        int restore = await DotNetCli.RestoreAsync(project.CsprojPath, line => log.AppendLine(line), TestContext.Current.CancellationToken);
        Assert.SkipWhen(restore != 0, $"Restoring Godot.NET.Sdk failed, probably because there is no network access:\n{log}");
        var compiler = new GraphCompiler(NodeLibrary.CreateDefault(project.References.AssemblyPaths));
        string generator = Assert.Single(project.UnapprovedSourceGenerators());
        Assert.EndsWith("Godot.SourceGenerators.dll", generator, StringComparison.Ordinal);

        Assert.Contains(project.PreviewGeneration(compiler).Diagnostics, diagnostic => diagnostic.IsError && diagnostic.Message.Contains("PropertyName", StringComparison.Ordinal));

        project.ApprovedSourceGenerators = [new(generator, NodeLibraryLoader.HashFile(generator))];
        ProjectGenerationResult approved = project.PreviewGeneration(compiler);

        Assert.True(approved.Success, string.Join("\n", approved.Diagnostics));
        Assert.Empty(project.UnapprovedSourceGenerators());

        // A file that no longer has the approved hash is not run.
        project.ApprovedSourceGenerators = [new(generator, new string('0', 64))];
        ProjectGenerationResult changed = project.PreviewGeneration(compiler);
        Assert.Contains(changed.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.SourceGeneratorNotRun);
    }

    [Fact]
    public async Task ASignalIsDeclaredEmittedAndHandledInGodot()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.CreateGodot(directory.Path, "Signals");
        var log = new StringBuilder();
        void Log(string line)
        {
            lock (log)
            {
                log.AppendLine(line);
            }
        }

        int restore = await DotNetCli.RestoreAsync(project.CsprojPath, Log, TestContext.Current.CancellationToken);
        Assert.SkipWhen(restore != 0, $"Restoring Godot.NET.Sdk failed, probably because there is no network access:\n{log}");
        var library = NodeLibrary.CreateDefault(project.References.AssemblyPaths);

        // Built-in signals are C# events of GodotSharp, offered as On nodes.
        Assert.Equal("On BaseButton.Pressed", library.Find("dotnet:E:Godot.BaseButton.Pressed")?.DisplayName);
        Assert.Equal("On Area2D.BodyEntered", library.Find("dotnet:E:Godot.Area2D.BodyEntered")?.DisplayName);

        // Main declares Greeted(string text). _Ready, after its greeting, handles Greeted by printing the text, then emits it.
        GraphDocument main = project.GraphDocuments.Single(graph => graph.Name == "Main");
        var editor = new GraphEditor(main, library);
        GraphSignal greeted = editor.AddSignal();
        editor.RenameSignal(greeted.Id, "Greeted");
        Assert.True(editor.SetSignalParameters(greeted.Id, "string text"));
        string text = greeted.Parameters[0].Id;
        GraphDocument ready = project.GraphDocuments.Single(graph => graph.Name == "_Ready");
        GraphNode greeting = ready.Nodes.Single(node => node.Type == "dotnet:M:Godot.GD.Print(System.String)");
        GraphNode Add(string type, string? value = null)
        {
            var node = new GraphNode { Id = GraphDocument.NewId(), Type = type };
            if (value is not null)
            {
                node.Properties["value"] = value;
            }

            if (SignalNodes.IsSignalNode(type))
            {
                node.Properties[TypeNodes.TypeProperty] = main.Id;
                node.Properties[SignalNodes.SignalProperty] = greeted.Id;
            }

            ready.Nodes.Add(node);
            return node;
        }

        void Wire(GraphNode from, string fromPin, GraphNode to, string toPin) =>
            ready.Connections.Add(new GraphConnection(new PinRef(from.Id, fromPin), new PinRef(to.Id, toPin)));
        GraphNode self = Add(TypeNodes.SelfTypeId);
        GraphNode on = Add(SignalNodes.OnTypeId);
        GraphNode heard = Add("dotnet:M:Godot.GD.Print(System.String)");
        GraphNode emit = Add(SignalNodes.EmitTypeId);
        Wire(greeting, "then", on, "exec");
        Wire(self, "self", on, "instance");
        Wire(on, "handler", heard, "exec");
        Wire(on, text, heard, "what");
        Wire(on, "then", emit, "exec");
        Wire(self, "self", emit, "instance");
        Wire(Add("literal.string", "signal received"), "value", emit, text);

        string generator = Assert.Single(project.UnapprovedSourceGenerators());
        project.ApprovedSourceGenerators = [new SourceGeneratorAssembly(generator, NodeLibraryLoader.HashFile(generator))];
        ProjectGenerationResult result = project.Generate(new GraphCompiler(library));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.True(await DotNetCli.BuildAsync(project.CsprojPath, "Debug", Log, TestContext.Current.CancellationToken) == 0, log.ToString());

        string? godot = Environment.GetEnvironmentVariable("SCRIPTART_GODOT");
        Assert.SkipWhen(string.IsNullOrEmpty(godot) || !File.Exists(godot), "Built; SCRIPTART_GODOT does not name a Godot 4 .NET executable to run it.");
        using DotNetProcess game = DotNetProcess.StartGodot(godot!, project.DirectoryPath, Log, ["--headless", "--quit-after", "30"]);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await game.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            game.Stop();
        }

        Assert.Contains("signal received", log.ToString(), StringComparison.Ordinal);
    }
}
