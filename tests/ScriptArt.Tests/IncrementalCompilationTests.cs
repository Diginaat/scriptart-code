using ScriptArt.Compiler;
using ScriptArt.Compiler.Emit;
using ScriptArt.Compiler.Nodes;
using ScriptArt.GraphModel;
using ScriptArt.NodeEditor;
using ScriptArt.ProjectSystem;

namespace ScriptArt.Tests;

public sealed class IncrementalCompilationTests
{
    private static readonly GraphCompiler Compiler = new(NodeLibrary.CreateDefault());

    // A program that prints the result of Greet(name), and Greet, which returns its argument.
    private static (VisualProject Project, GraphDocument Program, GraphDocument Greet) Project(string directory)
    {
        VisualProject project = VisualProject.Create(directory, "Hello");
        GraphDocument greet = project.AddFunction("Greet").Graph;
        greet.Parameters.Add(new GraphParameter { Id = "who", Name = "who", Type = "string" });
        greet.ReturnType = "string";
        string entry = greet.Nodes[0].Id;
        greet.Nodes.Add(new GraphNode { Id = "return", Type = FunctionNodes.ReturnTypeId });
        greet.Connections.Add(new(new(entry, "then"), new("return", "exec")));
        greet.Connections.Add(new(new(entry, "who"), new("return", "value")));

        GraphDocument program = project.Graphs[0].Graph;
        program.Nodes.RemoveAll(node => node.Type != BuiltInNodes.EntryTypeId);
        program.Connections.Clear();
        string start = program.Nodes[0].Id;
        program.Nodes.Add(new GraphNode { Id = "call", Type = FunctionNodes.CallTypeId, Properties = { [FunctionNodes.FunctionProperty] = greet.Id } });
        program.Nodes.Add(new GraphNode { Id = "name", Type = "literal.string", Properties = { ["value"] = "Ada" } });
        program.Nodes.Add(new GraphNode { Id = "print", Type = "console.write-line" });
        program.Connections.Add(new(new(start, "then"), new("call", "exec")));
        program.Connections.Add(new(new("call", "then"), new("print", "exec")));
        program.Connections.Add(new(new("name", "value"), new("call", "who")));
        program.Connections.Add(new(new("call", "result"), new("print", "text")));
        return (project, program, greet);
    }

    private static string[] Texts(VisualProject project)
    {
        (var diagnostics, var sources) = project.CompileGraphs(Compiler);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.IsError);
        return [.. sources.Select(source => source.Text)];
    }

    /// <summary>
    /// Saves the project (which moves renamed graphs to their new files), then checks that compiling it incrementally gives what a
    /// cold compile of the saved files gives.
    /// </summary>
    private static void AssertMatchesFreshCompile(VisualProject project)
    {
        project.Save();
        Assert.Equal(Texts(VisualProject.Load(project.ProjectFilePath)), Texts(project));
    }

    [Fact]
    public void UnchangedGraphsAreReusedAndEditedOnesRecompiled()
    {
        using var directory = new TemporaryDirectory();
        (VisualProject project, GraphDocument program, _) = Project(directory.Path);
        GraphCompilationCache cache = project.CompilationCache;

        Texts(project);
        Assert.Equal((2, 0), (cache.CompiledCount, cache.ReusedCount));
        Texts(project);
        Assert.Equal((2, 2), (cache.CompiledCount, cache.ReusedCount));

        program.FindNode("name")!.Properties["value"] = "Grace";
        string[] texts = Texts(project);
        Assert.Equal((3, 3), (cache.CompiledCount, cache.ReusedCount));
        Assert.Contains("Greet(\"Grace\")", texts[0], StringComparison.Ordinal);
        AssertMatchesFreshCompile(project);
    }

    [Fact]
    public void ChangingASignatureRecompilesEveryGraph()
    {
        using var directory = new TemporaryDirectory();
        (VisualProject project, _, GraphDocument greet) = Project(directory.Path);
        Texts(project);

        greet.Name = "Welcome";
        string[] renamed = Texts(project);
        Assert.Equal(4, project.CompilationCache.CompiledCount);
        Assert.Contains("Welcome(\"Ada\")", renamed[0], StringComparison.Ordinal);
        AssertMatchesFreshCompile(project);

        // Making Greet wait makes its caller async too, though the caller's own graph did not change.
        GraphNode delay = new() { Id = "delay", Type = "dotnet:M:System.Threading.Tasks.Task.Delay(System.Int32)" };
        Assert.NotNull(Compiler.Library.Find(delay.Type));
        greet.Nodes.Add(delay);
        greet.Nodes.Add(new GraphNode { Id = "ms", Type = "literal.int", Properties = { ["value"] = "1" } });
        string entry = greet.Nodes[0].Id;
        greet.Connections.RemoveAll(connection => connection.From.Node == entry && connection.From.Pin == "then");
        greet.Connections.Add(new(new(entry, "then"), new("delay", "exec")));
        greet.Connections.Add(new(new("delay", "then"), new("return", "exec")));
        greet.Connections.Add(new(new("ms", "value"), new("delay", "millisecondsDelay")));
        string[] async = Texts(project);
        Assert.Contains("await Welcome(\"Ada\")", async[0], StringComparison.Ordinal);
        AssertMatchesFreshCompile(project);
    }

    [Fact]
    public void SnapshotsAreIndependentReadOnlyCopiesThatShareTheCache()
    {
        using var directory = new TemporaryDirectory();
        (VisualProject project, GraphDocument program, _) = Project(directory.Path);
        VisualProject snapshot = project.Snapshot();

        program.FindNode("name")!.Properties["value"] = "Changed later";

        Assert.True(snapshot.IsSnapshot);
        Assert.Contains("Greet(\"Ada\")", Texts(snapshot)[0], StringComparison.Ordinal);
        Assert.Same(project.CompilationCache, snapshot.CompilationCache);
        Assert.Throws<InvalidOperationException>(snapshot.Save);
        Assert.Throws<InvalidOperationException>(() => snapshot.AddFunction("Other"));
    }

    [Fact]
    public void CloneCopiesEverythingAndSharesNothingMutable()
    {
        using var directory = new TemporaryDirectory();
        (_, GraphDocument program, _) = Project(directory.Path);
        program.Comments.Add(new GraphComment { Id = "c", Text = "Note", Width = 10, Height = 10 });
        program.Variables.Add(new GraphVariable { Id = "v", Name = "count", Type = "int", Value = "1" });
        program.Connections[0].Points = [new GraphPoint(1, 2)];

        GraphDocument copy = program.Clone();
        Assert.Equal(GraphSerializer.Serialize(program), GraphSerializer.Serialize(copy));

        copy.Nodes[0].Properties["x"] = "y";
        copy.Connections[0].Points!.Add(new GraphPoint(3, 4));
        copy.Variables[0].Name = "other";
        Assert.NotEqual(GraphSerializer.Serialize(program), GraphSerializer.Serialize(copy));
        Assert.Single(program.Connections[0].Points!);
        Assert.Equal("count", program.Variables[0].Name);
    }

    [Fact]
    public void ReferencedAssemblyMetadataIsReadOncePerFileVersion()
    {
        string path = typeof(GraphDocument).Assembly.Location;
        Assert.Same(RoslynVerifier.FileReference(path), RoslynVerifier.FileReference(path));
    }
}

public sealed class SpatialIndexTests
{
    [Fact]
    public void QueriesFindExactlyTheIntersectingItems()
    {
        var random = new Random(7);
        var index = new SpatialIndex<int>(100);
        var areas = new Dictionary<int, Area>();
        for (int i = 0; i < 500; i++)
        {
            areas[i] = new Area(random.Next(-2000, 2000), random.Next(-2000, 2000), random.Next(0, 400), random.Next(0, 400));
            index.Set(i, areas[i]);
        }

        // Move some, remove some, as dragging and deleting do.
        for (int i = 0; i < 100; i++)
        {
            areas[i] = areas[i] with { X = areas[i].X + 333, Y = areas[i].Y - 77 };
            index.Set(i, areas[i]);
        }

        for (int i = 100; i < 150; i++)
        {
            Assert.True(index.Remove(i));
            areas.Remove(i);
        }

        foreach (Area query in (Area[])[new(0, 0, 0, 0), new(-500, -500, 300, 900), new(-1e6, -1e6, 2e6, 2e6), new(5000, 5000, 10, 10)])
        {
            int[] expected = [.. areas.Where(item => item.Value.Intersects(query)).Select(item => item.Key).Order()];
            Assert.Equal(expected, index.Query(query).Order());
        }

        Assert.Equal(450, index.Count);
        Assert.False(index.Remove(100));
        index.Clear();
        Assert.Empty(index.Query(new Area(-1e6, -1e6, 2e6, 2e6)));
    }
}
