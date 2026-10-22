using ScriptArt.Compiler;
using ScriptArt.Compiler.Diagnostics;
using ScriptArt.Compiler.Nodes;
using ScriptArt.Compiler.TypeSystem;
using ScriptArt.GraphModel;
using ScriptArt.NodeEditor;

namespace ScriptArt.Tests;

/// <summary>Dictionaries, and lists of the project's classes and structs.</summary>
public sealed class DictionaryTests
{
    private static readonly NodeLibrary Library = NodeLibrary.CreateDefault();

    private const string Scores = "Dictionary<string, int>";

    [Fact]
    public void EveryOfferedTypeNameReadsBackAsTheSameType()
    {
        GraphBuilder point = GraphBuilder.Type("Point", GraphKind.Struct);
        IReadOnlyList<TypeRef> types = TypeRef.TypesOf([point.Graph]);

        Assert.All(types, type => Assert.Equal(type, TypeRef.FromName(type.DisplayName, [point.Graph])));
        Assert.Contains(TypeRef.DictionaryOf(TypeRef.String, TypeRef.ProjectType("Point")), types);
        Assert.Contains(TypeRef.ListOf(TypeRef.ProjectType("Point")), types);
        Assert.Equal("Dictionary<int, List<int>>", TypeRef.DictionaryOf(TypeRef.Int32, TypeRef.ListOf(TypeRef.Int32)).DisplayName);
    }

    [Theory]
    [InlineData("Dictionary<double, int>")]
    [InlineData("Dictionary<string>")]
    [InlineData("List<int")]
    [InlineData("List<int, string>")]
    [InlineData("List<Missing>")]
    [InlineData("Dictionary<string, Missing>")]
    public void UnsupportedCollectionNamesAreNotTypes(string name) => Assert.Null(TypeRef.FromName(name));

    [Fact]
    public void DictionaryVariableIsFilledReadAndLoopedOverByKey()
    {
        var graph = new GraphBuilder();
        string scores = graph.Variable("scores", Scores, "");
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string set = graph.CollectionNode("dictionary.set", Scores);
        string remove = graph.CollectionNode("dictionary.remove", Scores);
        string branch = graph.Node("core.branch");
        string contains = graph.CollectionNode("dictionary.contains-key", Scores);
        string printValue = graph.Node("console.write-line");
        string value = graph.CollectionNode("dictionary.get", Scores);
        string valueText = graph.Node("convert.int-to-string");
        string loop = graph.Node("flow.foreach.string");
        string keys = graph.CollectionNode("dictionary.keys", Scores);
        string printKey = graph.Node("console.write-line");
        graph.Then(entry, set).Then(set, remove).Then(remove, branch).Then(branch, printValue, "true").Then(branch, loop, "false").Then(loop, printKey, "body")
            .Connect(graph.VariableNode(scores, set: false), "value", set, "dictionary")
            .Connect(graph.Node("literal.string", "Ada"), "value", set, "key")
            .Connect(graph.Node("literal.int", "3"), "value", set, "value")
            .Connect(graph.VariableNode(scores, set: false), "value", remove, "dictionary")
            .Connect(graph.Node("literal.string", "Bob"), "value", remove, "key")
            .Connect(graph.VariableNode(scores, set: false), "value", contains, "dictionary")
            .Connect(graph.Node("literal.string", "Ada"), "value", contains, "key")
            .Connect(contains, "result", branch, "condition")
            .Connect(graph.VariableNode(scores, set: false), "value", value, "dictionary")
            .Connect(graph.Node("literal.string", "Ada"), "value", value, "key")
            .Connect(graph.Node("literal.int", "0"), "value", value, "default")
            .Connect(value, "value", valueText, "value")
            .Connect(valueText, "text", printValue, "text")
            .Connect(graph.VariableNode(scores, set: false), "value", keys, "dictionary")
            .Connect(keys, "keys", loop, "list")
            .Connect(loop, "item", printKey, "text");

        Assert.Equal("""
            var scores = new Dictionary<string, int>();
            scores["Ada"] = 3;
            scores.Remove("Bob");
            if (scores.ContainsKey("Ada"))
            {
            Console.WriteLine(scores.GetValueOrDefault("Ada", 0).ToString());
            }
            else
            {
            foreach (string item in new List<string>(scores.Keys))
            {
            Console.WriteLine(item);
            }
            }
            """, VariableTests.MainBody(graph));
    }

    [Fact]
    public void ListsAndDictionariesOfProjectTypesGenerateTypedCollections()
    {
        GraphBuilder point = GraphBuilder.Type("Point", GraphKind.Struct);
        point.Property("X", "int", "0");
        GraphBuilder shape = GraphBuilder.Type("Shape");
        shape.Property("Corners", "List<Point>");
        shape.Property("Named", "Dictionary<string, Point>");
        var program = new GraphBuilder();
        string shapes = program.Variable("shapes", "List<Shape>", "");
        string entry = program.Node(BuiltInNodes.EntryTypeId);
        string create = program.TypeNode(TypeNodes.CreateTypeId, shape.Graph);
        string add = program.CollectionNode("list.add", "List<Shape>");
        string loop = program.CollectionNode("flow.foreach", "List<Shape>");
        string corners = program.TypeNode(TypeNodes.GetTypeId, shape.Graph, shape.Graph.Properties[0].Id);
        string addCorner = program.CollectionNode("list.add", "List<Point>");
        string count = program.CollectionNode("list.count", "List<Shape>");
        string print = program.Node("console.write-line");
        string text = program.Node("convert.int-to-string");
        program.Then(entry, create).Then(create, add).Then(add, loop).Then(loop, addCorner, "body").Then(loop, print, "completed")
            .Connect(program.VariableNode(shapes, set: false), "value", add, "list")
            .Connect(create, "value", add, "item")
            .Connect(program.VariableNode(shapes, set: false), "value", loop, "list")
            .Connect(loop, "item", corners, "object")
            .Connect(corners, "value", addCorner, "list")
            .Connect(program.TypeNode(TypeNodes.CreateTypeId, point.Graph), "value", addCorner, "item")
            .Connect(program.VariableNode(shapes, set: false), "value", count, "list")
            .Connect(count, "count", text, "value")
            .Connect(text, "text", print, "text");

        string[] sources = TypeTests.CompileProject(program, shape, point);

        Assert.Contains("public List<Point> Corners { get; set; } = new List<Point>();", sources[1], StringComparison.Ordinal);
        Assert.Contains("public Dictionary<string, Point> Named { get; set; } = new Dictionary<string, Point>();", sources[1], StringComparison.Ordinal);
        Assert.Contains("var shapes = new List<Shape>();", sources[0], StringComparison.Ordinal);
        Assert.Contains("shapes.Add(shape);", sources[0], StringComparison.Ordinal);
        Assert.Contains("foreach (Shape item in shapes)", sources[0], StringComparison.Ordinal);
        Assert.Contains("item.Corners.Add(new Point());", sources[0], StringComparison.Ordinal);
    }

    [Fact]
    public void PaletteOffersNodesOfTheCollectionTypesTheProjectUses()
    {
        GraphBuilder account = GraphBuilder.Type("Account");
        account.Property("Tags", "Dictionary<int, string>");
        var program = new GraphBuilder();
        program.Variable("accounts", "List<Account>", "");
        var editor = new GraphEditor(program.Graph, Library) { ProjectGraphs = [program.Graph, account.Graph] };

        string[] offered = [.. editor.AvailableDefinitions.Select(definition => definition.DisplayName)];

        Assert.Contains("Add to List (Account)", offered);
        Assert.Contains("For Each (Account)", offered);
        Assert.Contains("Set Entry (int, string)", offered);
        Assert.DoesNotContain("Set Entry (string, int)", offered);
        Assert.DoesNotContain(offered, name => name.Contains("(Point)", StringComparison.Ordinal));

        GraphNode added = editor.AddNode(editor.AvailableDefinitions.First(definition => definition.DisplayName == "Add to List (Account)"), 0, 0);
        Assert.Equal("List<Account>", added.Properties[CollectionNodes.CollectionProperty]);
        Assert.Equal("list.add", added.Type);
    }

    [Fact]
    public void CollectionNodeOfAMissingTypeIsReportedOnTheNode()
    {
        var program = new GraphBuilder();
        string entry = program.Node(BuiltInNodes.EntryTypeId);
        string clear = program.CollectionNode("list.clear", "List<Gone>");
        program.Then(entry, clear);

        GraphCompilationResult result = TypeTests.Compile(program, program);

        Assert.False(result.Success);
        GraphDiagnostic diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UnknownType);
        Assert.Equal(clear, diagnostic.NodeId);
    }

    [Fact]
    public void DictionaryStartsEmpty()
    {
        Assert.NotNull(LiteralValues.TryInitialValue(TypeRef.FromName(Scores)!, "", out _));
        Assert.Null(LiteralValues.TryInitialValue(TypeRef.FromName(Scores)!, "a=1", out string? error));
        Assert.Contains("starts empty", error, StringComparison.Ordinal);
    }
}
