using Microsoft.CodeAnalysis;
using ScriptArt.Compiler;
using ScriptArt.Compiler.Emit;
using ScriptArt.Compiler.Nodes;
using ScriptArt.GraphModel;
using ScriptArt.ProjectSystem;

namespace ScriptArt.Tests;

public sealed class NodeLibraryTests
{
    // A node library as a third party writes it: it references only the SDK and the graph model.
    private const string ShoutLibrary = """
        using ScriptArt.Compiler.Ir;
        using ScriptArt.Compiler.Lowering;
        using ScriptArt.Compiler.Nodes;
        using ScriptArt.Compiler.TypeSystem;

        namespace Acme;

        public sealed class Shout() : PureNodeDefinition("acme.shout", "Shout", "Acme", "Adds an exclamation mark.",
            [PinDefinition.DataIn("text", "Text", TypeRef.String), PinDefinition.DataOut("result", "Result", TypeRef.String)])
        {
            public override IrExpression Lower(ExpressionLoweringContext context) =>
                new IrStaticCall(TypeRef.String, "Concat", [context.Input("text"), new IrLiteral("!", TypeRef.String)], TypeRef.String);
        }

        public sealed class Nodes : INodeProvider
        {
            public IEnumerable<NodeDefinition> CreateNodes() => [new Shout()];
        }
        """;

    /// <summary>Compiles <paramref name="source"/> into a library assembly in <paramref name="directory"/> and returns its path.</summary>
    private static string BuildLibrary(string directory, string source, string name = "Acme.Nodes")
    {
        Microsoft.CodeAnalysis.CSharp.CSharpCompilation compilation = RoslynVerifier.CreateCompilation(name,
            [new VerificationSource("Library.cs", "global using System;\nglobal using System.Collections.Generic;\nglobal using System.Linq;\n" + source)],
            isExecutable: false, [typeof(NodeDefinition).Assembly.Location, typeof(GraphDocument).Assembly.Location]);
        string path = Path.Combine(directory, name + ".dll");
        Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit(path);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
        return path;
    }

    [Fact]
    public void LibraryNodesAreLoadedRegisteredAndGenerateCode()
    {
        using var directory = new TemporaryDirectory();
        NodeLibraryContents contents = NodeLibraryLoader.Load(BuildLibrary(directory.Path, ShoutLibrary));

        Assert.Empty(contents.Problems);
        NodeDefinition shout = Assert.Single(contents.Definitions);
        Assert.Equal("acme.shout", shout.TypeId);

        NodeLibrary library = NodeLibrary.CreateDefault();
        int version = library.Version;
        Assert.Empty(library.RegisterLibrary(contents));
        Assert.NotEqual(version, library.Version);

        var program = new GraphBuilder();
        string entry = program.Node(BuiltInNodes.EntryTypeId);
        string print = program.Node("console.write-line");
        string node = program.Node("acme.shout");
        program.Then(entry, print)
            .Connect(program.Node("literal.string", "Hi"), "value", node, "text")
            .Connect(node, "result", print, "text");
        GraphCompilationResult result = new GraphCompiler(library).Compile(program.Graph, new GraphCompilationOptions("Demo"), "Graphs/Program.graph.json");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Console.WriteLine(string.Concat(\"Hi\", \"!\"));", result.Source!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void NodesWhoseTypeIdIsTakenAreSkippedAndReported()
    {
        using var directory = new TemporaryDirectory();
        NodeLibraryContents contents = NodeLibraryLoader.Load(BuildLibrary(directory.Path, ShoutLibrary.Replace("\"acme.shout\"", "\"console.write-line\"", StringComparison.Ordinal)));
        NodeLibrary library = NodeLibrary.CreateDefault();

        string problem = Assert.Single(library.RegisterLibrary(contents));

        Assert.Contains("console.write-line", problem, StringComparison.Ordinal);
        Assert.Equal("Print Line", library.Find("console.write-line")!.DisplayName);
    }

    [Fact]
    public void FaultyLibrariesAreReportedNotThrown()
    {
        using var directory = new TemporaryDirectory();
        string throwing = BuildLibrary(directory.Path, ShoutLibrary.Replace("=> [new Shout()];", "=> throw new InvalidOperationException(\"broken\");", StringComparison.Ordinal), "Throwing");
        string empty = BuildLibrary(directory.Path, "namespace Empty; public static class Nothing { }", "Empty");
        string notAssembly = Path.Combine(directory.Path, "Text.dll");
        File.WriteAllText(notAssembly, "not an assembly");

        Assert.Contains("broken", Assert.Single(NodeLibraryLoader.Load(throwing).Problems), StringComparison.Ordinal);
        Assert.Contains(nameof(INodeProvider), Assert.Single(NodeLibraryLoader.Load(empty).Problems), StringComparison.Ordinal);
        Assert.Contains("not a .NET assembly", Assert.Single(NodeLibraryLoader.Load(notAssembly).Problems), StringComparison.Ordinal);
        Assert.Contains("Missing.dll", Assert.Single(NodeLibraryLoader.Load(Path.Combine(directory.Path, "Missing.dll")).Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyApprovedUnchangedLibrariesAreLoaded()
    {
        using var directory = new TemporaryDirectory();
        string settings = Path.Combine(directory.Path, "settings", "node-libraries.json");
        string path = BuildLibrary(directory.Path, ShoutLibrary);

        NodeLibraryTrust trust = NodeLibraryTrust.Load(settings);
        Assert.Empty(trust.Libraries);
        ApprovedNodeLibrary approved = trust.Approve(path);
        Assert.Equal(NodeLibraryLoader.HashFile(path), approved.Sha256);

        NodeLibraryTrust reloaded = NodeLibraryTrust.Load(settings);
        Assert.Equal(approved, Assert.Single(reloaded.Libraries));
        Assert.Single(Assert.Single(reloaded.LoadApproved()).Definitions);

        // A changed file is not loaded until approved again.
        File.AppendAllText(path, "tampered");
        NodeLibraryContents changed = Assert.Single(reloaded.LoadApproved());
        Assert.Empty(changed.Definitions);
        Assert.Contains("changed since it was approved", Assert.Single(changed.Problems), StringComparison.Ordinal);

        Assert.True(reloaded.Remove(path));
        Assert.Empty(NodeLibraryTrust.Load(settings).Libraries);
        Assert.Throws<ArgumentException>(() => reloaded.Approve(Path.Combine(directory.Path, "settings", "node-libraries.json")));
    }
}
