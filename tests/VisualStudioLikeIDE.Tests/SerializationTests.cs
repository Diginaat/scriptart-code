using VisualStudioLikeIDE.Compiler.Nodes;
using VisualStudioLikeIDE.GraphModel;
using VisualStudioLikeIDE.ProjectSystem;

namespace VisualStudioLikeIDE.Tests;

public sealed class SerializationTests
{
    private static GraphDocument SampleGraph()
    {
        var graph = new GraphBuilder();
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        graph.Then(entry, graph.PrintLiteral("Hi \"there\""));
        graph.Graph.Nodes[0].X = 12.5;
        graph.Graph.Nodes[0].Y = -40;
        return graph.Graph;
    }

    [Fact]
    public void GraphRoundTripsWithoutLoss()
    {
        GraphDocument original = SampleGraph();
        string json = GraphSerializer.Serialize(original);
        GraphDocument loaded = GraphSerializer.Deserialize(json);

        Assert.Equal(json, GraphSerializer.Serialize(loaded));
        Assert.Equal(original.Connections, loaded.Connections);
        Assert.Equal(12.5, loaded.Nodes[0].X);
        Assert.Equal("Hi \"there\"", loaded.Nodes[2].Properties["value"]);
    }

    [Fact]
    public void SerializationIsDeterministicAndUsesLineFeeds()
    {
        GraphDocument graph = SampleGraph();
        graph.Nodes[1].Properties["zeta"] = "1";
        graph.Nodes[1].Properties["alpha"] = "2";
        string json = GraphSerializer.Serialize(graph);

        Assert.Equal(json, GraphSerializer.Serialize(GraphSerializer.Deserialize(json)));
        Assert.DoesNotContain("\r", json, StringComparison.Ordinal);
        Assert.True(json.IndexOf("\"alpha\"", StringComparison.Ordinal) < json.IndexOf("\"zeta\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Version1FilesMigrateToCurrentVersion()
    {
        GraphDocument graph = GraphSerializer.Deserialize("""{ "schemaVersion": 1, "id": "g", "name": "P", "nodes": [], "connections": [] }""");

        Assert.Equal(GraphDocument.CurrentSchemaVersion, graph.SchemaVersion);
        Assert.Empty(graph.Comments);
        Assert.Contains($"\"schemaVersion\": {GraphDocument.CurrentSchemaVersion}", GraphSerializer.Serialize(graph), StringComparison.Ordinal);
    }

    [Fact]
    public void ReroutePointsRoundTripAndDirectWiresOmitThem()
    {
        GraphDocument graph = GraphDocument.CreateEmpty("Program", GraphKind.EntryPoint);
        graph.Connections.Add(new GraphConnection(new PinRef("a", "value"), new PinRef("b", "text")) { Points = [new GraphPoint(10, 20), new GraphPoint(30, 40)] });
        graph.Connections.Add(new GraphConnection(new PinRef("a", "then"), new PinRef("b", "exec")));

        string json = GraphSerializer.Serialize(graph);
        GraphDocument loaded = GraphSerializer.Deserialize(json);

        Assert.Equal([new GraphPoint(10, 20), new GraphPoint(30, 40)], loaded.Connections[0].Points);
        Assert.Null(loaded.Connections[1].Points);
        // Only the rerouted wire writes a points array.
        Assert.Equal(2, json.Split("\"points\"").Length);
    }

    [Fact]
    public void CommentsRoundTrip()
    {
        GraphDocument graph = GraphDocument.CreateEmpty("Program", GraphKind.EntryPoint);
        graph.Comments.Add(new GraphComment { Id = "c1", X = 10, Y = 20, Width = 300, Height = 150, Text = "Read input" });

        GraphComment comment = Assert.Single(GraphSerializer.Deserialize(GraphSerializer.Serialize(graph)).Comments);

        Assert.Equal(("c1", 10.0, 20.0, 300.0, 150.0, "Read input"), (comment.Id, comment.X, comment.Y, comment.Width, comment.Height, comment.Text));
    }

    [Fact]
    public void Version1FileFromDiskLoads()
    {
        // A literal schema-version-1 file. Keep this test unchanged when the schema evolves: old files must keep loading.
        const string version1 = """
            {
              "schemaVersion": 1,
              "id": "g1",
              "name": "Program",
              "kind": "EntryPoint",
              "nodes": [
                { "id": "a", "type": "core.entry", "x": 0, "y": 0, "properties": {} },
                { "id": "b", "type": "console.write-line", "x": 200, "y": 0, "properties": {} },
                { "id": "c", "type": "literal.string", "x": 0, "y": 100, "properties": { "value": "v1" } }
              ],
              "connections": [
                { "from": { "node": "a", "pin": "then" }, "to": { "node": "b", "pin": "exec" } },
                { "from": { "node": "c", "pin": "value" }, "to": { "node": "b", "pin": "text" } }
              ]
            }
            """;

        GraphDocument graph = GraphSerializer.Deserialize(version1);
        Assert.Equal(3, graph.Nodes.Count);
        Assert.Equal(2, graph.Connections.Count);
        Assert.Equal("v1", graph.FindNode("c")!.Properties["value"]);
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 99, "id": "g", "name": "P" }""", "but this version of the IDE supports up to")]
    [InlineData("""{ "id": "g", "name": "P" }""", "no schemaVersion")]
    [InlineData("""[1, 2]""", "JSON object")]
    [InlineData("""{ "schemaVersion": 1, "name": "P" """, "not valid JSON")]
    [InlineData("""{ "schemaVersion": 1, "name": "P" }""", "not valid")]
    public void InvalidFilesReportClearErrors(string json, string expectedMessage)
    {
        GraphFormatException error = Assert.Throws<GraphFormatException>(() => GraphSerializer.Deserialize(json));
        Assert.Contains(expectedMessage, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AtomicWriteLeavesNoTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "sub", "graph.json");
        AtomicFile.WriteAllText(path, "one");
        AtomicFile.WriteAllText(path, "two");

        Assert.Equal("two", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void ProjectRoundTrips()
    {
        var project = new ProjectDocument { Name = "App", RootNamespace = "App.Core" };
        project.Graphs.Add("Graphs/Program.graph.json");

        ProjectDocument loaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));
        Assert.Equal("App.Core", loaded.RootNamespace);
        Assert.Equal(["Graphs/Program.graph.json"], loaded.Graphs);
    }
}
