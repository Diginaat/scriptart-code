using VisualStudioLikeIDE.Compiler;
using VisualStudioLikeIDE.Compiler.Diagnostics;
using VisualStudioLikeIDE.Compiler.Emit;
using VisualStudioLikeIDE.Compiler.Nodes;
using VisualStudioLikeIDE.GraphModel;
using VisualStudioLikeIDE.NodeEditor;

namespace VisualStudioLikeIDE.Tests;

public sealed class TypeTests
{
    private static readonly GraphCompiler Compiler = new(NodeLibrary.CreateDefault());

    private static GraphCompilationResult Compile(GraphBuilder graph, params GraphBuilder[] project) =>
        Compiler.Compile(graph.Graph, new GraphCompilationOptions("Demo", [.. project.Select(other => other.Graph)]), "Graphs/Test.graph.json");

    /// <summary>Compiles every graph of the project, checks Roslyn accepts them together, and returns each file's text.</summary>
    private static string[] CompileProject(params GraphBuilder[] project)
    {
        GeneratedSource[] sources = [.. project.Select(graph =>
        {
            GraphCompilationResult result = Compile(graph, project);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            return result.Source!;
        })];
        IReadOnlyList<GraphDiagnostic> roslyn = RoslynVerifier.Verify("Demo",
            [.. sources.Select(source => new VerificationSource(source.RelativePath, source.Text, source))], isExecutable: true);
        Assert.Empty(roslyn);
        return [.. sources.Select(source => source.Text)];
    }

    /// <summary>The generated text after the header and namespace: the type declaration and its members.</summary>
    private static string TypeText(string source) => source[source.IndexOf("internal ", StringComparison.Ordinal)..].TrimEnd().ReplaceLineEndings("\n");

    [Fact]
    public void ClassGeneratesPropertiesWithInitialValuesOnlyWhereTheyDifferFromTheDefault()
    {
        GraphBuilder tag = GraphBuilder.Type("Tag");
        tag.Property("Label", "string", "new");
        GraphBuilder item = GraphBuilder.Type("Item");
        item.Property("Name", "string");
        item.Property("Count", "int", "0");
        item.Property("Price", "double", "2.5");
        item.Property("Sold", "bool", "false");
        item.Property("Scores", "List<int>");
        item.Property("Tag", "Tag");
        var program = new GraphBuilder();
        program.Then(program.Node(BuiltInNodes.EntryTypeId), program.PrintLiteral("Hi"));

        string[] sources = CompileProject(program, item, tag);

        Assert.Equal("""
            internal sealed class Item
            {
                public string Name { get; set; } = "";
                public int Count { get; set; }
                public double Price { get; set; } = 2.5;
                public bool Sold { get; set; }
                public List<int> Scores { get; set; } = new List<int>();
                public Tag Tag { get; set; } = new Tag();
            }
            """, TypeText(sources[1]));
        Assert.Contains("using System.Collections.Generic;", sources[1], StringComparison.Ordinal);
        Assert.Equal("""
            internal sealed class Tag
            {
                public string Label { get; set; } = "new";
            }
            """, TypeText(sources[2]));
    }

    [Fact]
    public void StructWithInitialValuesGetsAParameterlessConstructor()
    {
        GraphBuilder point = GraphBuilder.Type("Point", GraphKind.Struct);
        point.Property("X", "int", "0");
        point.Property("Y", "int", "0");
        GraphBuilder named = GraphBuilder.Type("Named", GraphKind.Struct);
        named.Property("Name", "string");
        var program = new GraphBuilder();
        program.Then(program.Node(BuiltInNodes.EntryTypeId), program.PrintLiteral("Hi"));

        string[] sources = CompileProject(program, point, named);

        Assert.Equal("""
            internal struct Point
            {
                public int X { get; set; }
                public int Y { get; set; }
            }
            """, TypeText(sources[1]));
        Assert.Equal("""
            internal struct Named
            {
                public Named()
                {
                }

                public string Name { get; set; } = "";
            }
            """, TypeText(sources[2]));
    }

    [Fact]
    public void ClassObjectsAreCreatedOnceAndTheirPropertiesAssigned()
    {
        GraphBuilder counter = GraphBuilder.Type("Counter");
        string count = counter.Property("Count", "int", "0");
        var program = new GraphBuilder();
        string entry = program.Node(BuiltInNodes.EntryTypeId);
        string create = program.TypeNode(TypeNodes.CreateTypeId, counter.Graph);
        string set = program.TypeNode(TypeNodes.SetTypeId, counter.Graph, count);
        string get = program.TypeNode(TypeNodes.GetTypeId, counter.Graph, count);
        string add = program.Node("math.int.add");
        string print = program.Node("console.write-line");
        string toText = program.Node("convert.int-to-string");
        string countText = program.TypeNode(TypeNodes.GetTypeId, counter.Graph, count);
        program.Then(entry, create).Then(create, set).Then(set, print)
            .Connect(create, "value", set, "object")
            .Connect(create, "value", get, "object")
            .Connect(get, "value", add, "a")
            .Connect(program.Node("literal.int", "1"), "value", add, "b")
            .Connect(add, "result", set, "value")
            .Connect(create, "value", countText, "object")
            .Connect(countText, "value", toText, "value")
            .Connect(toText, "text", print, "text");

        string[] sources = CompileProject(program, counter);

        Assert.Equal("""
            internal static class Program
            {
                public static void Main()
                {
                    var counter = new Counter();
                    counter.Count = counter.Count + 1;
                    Console.WriteLine(counter.Count.ToString());
                }
            }
            """, TypeText(sources[0]));
    }

    [Fact]
    public void StructValuesAreChangedByCopyingThemIntoAVariable()
    {
        GraphBuilder point = GraphBuilder.Type("Point", GraphKind.Struct);
        string x = point.Property("X", "int", "0");
        var program = new GraphBuilder();
        string variable = program.Variable("position", "Point", "");
        string entry = program.Node(BuiltInNodes.EntryTypeId);
        string set = program.VariableNode(variable, set: true);
        string with = program.TypeNode(TypeNodes.WithTypeId, point.Graph, x);
        string print = program.Node("console.write-line");
        string toText = program.Node("convert.int-to-string");
        string getX = program.TypeNode(TypeNodes.GetTypeId, point.Graph, x);
        program.Then(entry, set).Then(set, print)
            .Connect(program.VariableNode(variable, set: false), "value", with, "object")
            .Connect(program.Node("literal.int", "3"), "value", with, "value")
            .Connect(with, "result", set, "value")
            .Connect(program.VariableNode(variable, set: false), "value", getX, "object")
            .Connect(getX, "value", toText, "value")
            .Connect(toText, "text", print, "text");

        string[] sources = CompileProject(program, point);

        Assert.Equal("""
            internal static class Program
            {
                public static void Main()
                {
                    var position = new Point();
                    position = position with { X = 3 };
                    Console.WriteLine(position.X.ToString());
                }
            }
            """, TypeText(sources[0]));
    }

    [Fact]
    public void FunctionsTakeAndReturnClassesAndStructs()
    {
        GraphBuilder point = GraphBuilder.Type("Point", GraphKind.Struct);
        point.Property("X", "int", "0");
        var origin = GraphBuilder.Function("Origin", "Point");
        string input = origin.Parameter("start", "Point");
        string entry = origin.Node(FunctionNodes.EntryTypeId);
        string result = origin.Node(FunctionNodes.ReturnTypeId);
        origin.Then(entry, result).Connect(entry, input, result, "value");
        var program = new GraphBuilder();
        string main = program.Node(BuiltInNodes.EntryTypeId);
        string call = program.Call(origin.Graph);
        program.Then(main, call).Connect(program.TypeNode(TypeNodes.CreateTypeId, point.Graph), "value", call, input);

        string[] sources = CompileProject(program, origin, point);

        Assert.Contains("Origin(new Point());", sources[0], StringComparison.Ordinal);
        Assert.Contains("private static Point Origin(Point start)", sources[1], StringComparison.Ordinal);
    }

    [Fact]
    public void PaletteOffersSetForClassesWithForStructsAndNothingInATypeGraph()
    {
        GraphBuilder counter = GraphBuilder.Type("Counter");
        counter.Property("Count", "int", "0");
        GraphBuilder point = GraphBuilder.Type("Point", GraphKind.Struct);
        point.Property("X", "int", "0");
        var program = new GraphBuilder();
        GraphDocument[] project = [program.Graph, counter.Graph, point.Graph];

        string[] names = [.. Compiler.Library.DefinitionsFor(program.Graph, project)
            .Where(definition => definition.Category == TypeNodes.Category).Select(definition => definition.DisplayName).Order(StringComparer.Ordinal)];

        Assert.Equal(["Get Counter.Count", "Get Point.X", "New Counter", "New Point", "Point With X", "Set Counter.Count"], names);
        Assert.Empty(Compiler.Library.DefinitionsFor(counter.Graph, project));
        Assert.IsType<CreateObjectNode>(Compiler.Library.DefinitionsFor(program.Graph, project).Single(definition => definition.DisplayName == "New Counter"));
        Assert.IsType<CreateStructNode>(Compiler.Library.DefinitionsFor(program.Graph, project).Single(definition => definition.DisplayName == "New Point"));
    }

    [Theory]
    [InlineData("Class")]
    [InlineData("Struct")]
    public void TypesThatContainThemselvesAreRejected(string kind)
    {
        GraphBuilder line = GraphBuilder.Type("Line", Enum.Parse<GraphKind>(kind));
        line.Property("Start", "Point");
        GraphBuilder point = GraphBuilder.Type("Point", Enum.Parse<GraphKind>(kind));
        point.Property("Owner", "Line");

        GraphCompilationResult result = Compile(line, line, point);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.InvalidType && diagnostic.Message.Contains("contain itself", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("X", "int", "", "initial value")]
    [InlineData("X", "Missing", "", "unsupported type 'Missing'")]
    [InlineData("Point", "int", "0", "cannot have the name of its class")]
    [InlineData("1X", "int", "0", "valid C# name")]
    [InlineData("X", "Point", "x", "Leave the initial value blank")]
    public void InvalidPropertiesAreReported(string name, string type, string value, string message)
    {
        GraphBuilder point = GraphBuilder.Type("Point");
        point.Property(name, type, value);
        GraphBuilder other = GraphBuilder.Type("Other");
        if (type == "Point")
        {
            point.Graph.Properties.Clear();
            other.Property(name, type, value);
            point = other;
        }

        GraphCompilationResult result = Compile(point, point, GraphBuilder.Type("Point"));

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.InvalidType && diagnostic.Message.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void TypeNamesMustBeUniqueAcrossGraphs()
    {
        GraphBuilder square = GraphBuilder.Type("Square");
        GraphCompilationResult result = Compile(square, square, GraphBuilder.Function("Square"));

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.InvalidType && diagnostic.Message.Contains("Another graph is named 'Square'", StringComparison.Ordinal));
    }

    [Fact]
    public void NodesOfDeletedPropertiesAndSetOnStructsAreReported()
    {
        GraphBuilder point = GraphBuilder.Type("Point", GraphKind.Struct);
        string x = point.Property("X", "int", "0");
        var program = new GraphBuilder();
        string entry = program.Node(BuiltInNodes.EntryTypeId);
        string set = program.TypeNode(TypeNodes.SetTypeId, point.Graph, x);
        string missing = program.TypeNode(TypeNodes.GetTypeId, point.Graph, "deleted");
        program.Then(entry, set);

        GraphCompilationResult result = Compile(program, program, point);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UnknownType && diagnostic.NodeId == set);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UnknownType && diagnostic.NodeId == missing);
    }

    [Fact]
    public void EditorChangesPropertiesAsUndoableSteps()
    {
        GraphBuilder point = GraphBuilder.Type("Point");
        var editor = new GraphEditor(point.Graph, Compiler.Library) { ProjectGraphs = [point.Graph] };

        GraphProperty property = editor.AddProperty("int");
        editor.SetPropertyValue(property.Id, "5");
        editor.RenameProperty(property.Id, "X");
        editor.SetPropertyType(property.Id, "Point");
        editor.RenameGraph("Location");

        Assert.Equal(("X", "Point", ""), (property.Name, property.Type, property.Value));
        Assert.Equal("Location", point.Graph.Name);
        editor.Undo();
        editor.Undo();
        Assert.Equal(("X", "int", "5"), (property.Name, property.Type, property.Value));
        editor.DeleteProperty(property.Id);
        Assert.Empty(point.Graph.Properties);
        editor.Undo();
        Assert.Same(property, Assert.Single(point.Graph.Properties));
        Assert.Throws<ArgumentException>(() => editor.AddProperty("Missing"));
    }
}
