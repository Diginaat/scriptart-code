using ScriptArt.Compiler;
using ScriptArt.Compiler.Nodes;
using ScriptArt.Compiler.TypeSystem;
using ScriptArt.GraphModel;
using ScriptArt.NodeEditor;
using ScriptArt.ProjectSystem;

namespace ScriptArt.Tests;

/// <summary>Constructors of classes and structs, generic classes and structs, and collections of collections.</summary>
public sealed class ConstructorAndGenericTests
{
    private static readonly NodeLibrary Library = NodeLibrary.CreateDefault();

    private static GraphBuilder Member(GraphBuilder owner, string name, GraphKind kind = GraphKind.Constructor, string? returnType = null)
    {
        var member = new GraphBuilder($"{owner.Graph.Name}-{name}", name, kind, returnType);
        member.Graph.Owner = owner.Graph.Id;
        return member;
    }

    private static string TypeText(string source) => source[source.IndexOf("internal ", StringComparison.Ordinal)..].TrimEnd().ReplaceLineEndings("\n");

    [Fact]
    public void ClassConstructorSetsPropertiesThroughSelfAndKeepsAParameterlessConstructor()
    {
        GraphBuilder point = GraphBuilder.Type("Point");
        string x = point.Property("X", "int", "0");
        GraphBuilder create = Member(point, "FromX");
        string value = create.Parameter("x", "int");
        string start = create.Node(FunctionNodes.EntryTypeId);
        string set = create.TypeNode(TypeNodes.SetTypeId, point.Graph, x);
        create.Then(start, set)
            .Connect(create.Node(TypeNodes.SelfTypeId), "self", set, "object")
            .Connect(start, value, set, "value");
        var program = new GraphBuilder();
        string entry = program.Node(BuiltInNodes.EntryTypeId);
        string made = program.With(program.TypeNode(TypeNodes.CreateTypeId, point.Graph), TypeNodes.ConstructorProperty, create.Graph.Id);
        string plain = program.TypeNode(TypeNodes.CreateTypeId, point.Graph);
        program.Then(entry, made).Then(made, plain)
            .Connect(program.Node("literal.int", "4"), "value", made, value);

        string[] sources = TypeTests.CompileProject(program, point, create);

        Assert.Contains("new Point(4);", sources[0], StringComparison.Ordinal);
        Assert.Contains("new Point();", sources[0], StringComparison.Ordinal);
        Assert.Equal("""
            internal sealed partial class Point
            {
                public Point()
                {
                }

                public int X { get; set; }
            }
            """, TypeText(sources[1]));
        Assert.Equal("""
            internal sealed partial class Point
            {
                public Point(int x)
                {
                    this.X = x;
                }
            }
            """, TypeText(sources[2]));
        Assert.Equal("Point.FromX.g.cs", GraphCompiler.GeneratedFileName(create.Graph, [point.Graph, create.Graph]));
    }

    [Fact]
    public void StructConstructorSetsItsOwnPropertiesAndIsAPureNewNode()
    {
        GraphBuilder size = GraphBuilder.Type("Size", GraphKind.Struct);
        string width = size.Property("Width", "int", "1");
        GraphBuilder create = Member(size, "Square");
        string side = create.Parameter("side", "int");
        string start = create.Node(FunctionNodes.EntryTypeId);
        string set = create.TypeNode(TypeNodes.SetTypeId, size.Graph, width);
        create.Then(start, set).Connect(start, side, set, "value");
        var program = new GraphBuilder();
        string entry = program.Node(BuiltInNodes.EntryTypeId);
        string print = program.Node("console.write-line");
        string get = program.TypeNode(TypeNodes.GetTypeId, size.Graph, width);
        string made = program.With(program.TypeNode(TypeNodes.CreateTypeId, size.Graph), TypeNodes.ConstructorProperty, create.Graph.Id);
        string text = program.Node("convert.int-to-string");
        program.Then(entry, print)
            .Connect(program.Node("literal.int", "3"), "value", made, side)
            .Connect(made, "value", get, "object")
            .Connect(get, "value", text, "value")
            .Connect(text, "text", print, "text");

        string[] sources = TypeTests.CompileProject(program, size, create);

        Assert.Contains("Console.WriteLine(new Size(3).Width.ToString());", sources[0], StringComparison.Ordinal);
        Assert.Contains("public Size()", sources[1], StringComparison.Ordinal);
        Assert.Contains("this.Width = side;", sources[2], StringComparison.Ordinal);

        // Outside its constructors a struct property still changes only by copying.
        var editor = new GraphEditor(program.Graph, Library) { ProjectGraphs = [program.Graph, size.Graph, create.Graph] };
        Assert.DoesNotContain(editor.AvailableDefinitions, definition => definition is SetPropertyNode);
        var inConstructor = new GraphEditor(create.Graph, Library) { ProjectGraphs = [program.Graph, size.Graph, create.Graph] };
        Assert.Contains(inConstructor.AvailableDefinitions, definition => definition is SetPropertyNode { OnSelf: true });
        Assert.Contains(editor.AvailableDefinitions, definition => definition.DisplayName == "New Size(side)");
        Assert.DoesNotContain(editor.AvailableDefinitions, definition => definition is FunctionCallNode);
    }

    [Fact]
    public void ConstructorsWithTheSameParameterTypesAreReported()
    {
        GraphBuilder point = GraphBuilder.Type("Point");
        GraphBuilder first = Member(point, "First");
        first.Parameter("x", "int");
        first.Node(FunctionNodes.EntryTypeId);
        GraphBuilder second = Member(point, "Second");
        second.Parameter("y", "int");
        second.Node(FunctionNodes.EntryTypeId);

        GraphCompilationResult result = TypeTests.Compile(second, point, first, second);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message == "Another constructor of Point takes the same parameter types as 'Second'.");
    }

    [Fact]
    public void GenericClassIsUsedWithATypeArgumentAndItsMethodsReturnTheTypeParameter()
    {
        GraphBuilder box = GraphBuilder.Type("Box");
        box.Graph.TypeParameters.Add("T");
        string content = box.Property("Content", "T");
        box.Property("History", "List<T>");
        GraphBuilder open = Member(box, "Open", GraphKind.Function, "T");
        string start = open.Node(FunctionNodes.EntryTypeId);
        string give = open.Node(FunctionNodes.ReturnTypeId);
        string read = open.With(open.TypeNode(TypeNodes.GetTypeId, box.Graph, content), TypeNodes.TypeArgumentsProperty, "T");
        open.Then(start, give)
            .Connect(open.Node(TypeNodes.SelfTypeId), "self", read, "object")
            .Connect(read, "value", give, "value");

        var program = new GraphBuilder();
        string boxes = program.Variable("boxes", "Box<string>", "");
        string entry = program.Node(BuiltInNodes.EntryTypeId);
        string set = program.With(program.TypeNode(TypeNodes.SetTypeId, box.Graph, content), TypeNodes.TypeArgumentsProperty, "string");
        string call = program.With(program.Call(open.Graph), TypeNodes.TypeArgumentsProperty, "string");
        string print = program.Node("console.write-line");
        program.Then(entry, set).Then(set, call).Then(call, print)
            .Connect(program.VariableNode(boxes, set: false), "value", set, "object")
            .Connect(program.Node("literal.string", "gift"), "value", set, "value")
            .Connect(program.VariableNode(boxes, set: false), "value", call, FunctionNodes.TargetPin)
            .Connect(call, "result", print, "text");

        string[] sources = TypeTests.CompileProject(program, box, open);

        Assert.Contains("var boxes = new Box<string>();", sources[0], StringComparison.Ordinal);
        Assert.Contains("string open = boxes.Open();", sources[0], StringComparison.Ordinal);
        Assert.Equal("""
            internal sealed partial class Box<T>
            {
                public T Content { get; set; } = default!;
                public List<T> History { get; set; } = new List<T>();
            }
            """, TypeText(sources[1]));
        Assert.Contains("public T Open()", sources[2], StringComparison.Ordinal);

        // The palette offers Box nodes for the arguments the project uses, and inside Box for its own type parameter.
        GraphDocument[] graphs = [program.Graph, box.Graph, open.Graph];
        Assert.Contains(new GraphEditor(program.Graph, Library) { ProjectGraphs = graphs }.AvailableDefinitions, definition => definition.DisplayName == "Set Box<string>.Content");
        Assert.Contains(new GraphEditor(open.Graph, Library) { ProjectGraphs = graphs }.AvailableDefinitions, definition => definition.DisplayName == "Get Box<T>.Content");
    }

    [Fact]
    public void TypeNamesNestAndGenericTypesNeedTheirArguments()
    {
        GraphBuilder pair = GraphBuilder.Type("Pair", GraphKind.Struct);
        pair.Graph.TypeParameters.AddRange(["TFirst", "TSecond"]);
        GraphDocument[] graphs = [pair.Graph];

        Assert.Equal(TypeRef.DictionaryOf(TypeRef.String, TypeRef.ListOf(TypeRef.ListOf(TypeRef.Int32))), TypeRef.FromName("Dictionary<string, List<List<int>>>"));
        Assert.Equal("Pair<int, List<string>>", TypeRef.FromName("Pair<int, List<string>>", graphs)!.DisplayName);
        Assert.Null(TypeRef.FromName("Pair<int>", graphs));
        Assert.Null(TypeRef.FromName("TFirst", graphs));
        Assert.Equal(TypeRef.TypeParameter("TFirst"), TypeRef.FromName("TFirst", graphs, pair.Graph));
        Assert.Equal("System.Text.StringBuilder", TypeRef.FromName("System.Text.StringBuilder")!.SourceName);
        Assert.Equal("StringBuilder", TypeRef.FromName("List<System.Text.StringBuilder>")!.ElementType!.DisplayName);
    }

    [Fact]
    public void ListOfListsHasGeneratedNodesForEachLevel()
    {
        var graph = new GraphBuilder();
        string grid = graph.Variable("grid", "List<List<int>>", "");
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string add = graph.CollectionNode("list.add", "List<List<int>>");
        string row = graph.Node("list.int.create");
        string count = graph.CollectionNode("list.count", "List<List<int>>");
        string print = graph.Node("console.write-line");
        string text = graph.Node("convert.int-to-string");
        graph.Then(entry, row).Then(row, add).Then(add, print)
            .Connect(graph.VariableNode(grid, set: false), "value", add, "list")
            .Connect(row, "list", add, "item")
            .Connect(graph.VariableNode(grid, set: false), "value", count, "list")
            .Connect(count, "count", text, "value")
            .Connect(text, "text", print, "text");

        Assert.Equal("""
            var grid = new List<List<int>>();
            var list = new List<int>();
            grid.Add(list);
            Console.WriteLine(grid.Count.ToString());
            """, VariableTests.MainBody(graph));
    }

    [Fact]
    public void AddedConstructorIsOwnedByItsTypeAndNamedAfterItOnDisk()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "Demo");
        ProjectGraph point = project.AddType("Point", GraphKind.Struct);

        ProjectGraph create = project.AddConstructor(point.Graph, "FromX");

        Assert.Equal("Graphs/Point.FromX.graph.json", create.RelativePath);
        Assert.Equal(GraphKind.Constructor, create.Graph.Kind);
        Assert.Equal(point.Graph.Id, create.Graph.Owner);
        Assert.Equal(FunctionNodes.EntryTypeId, Assert.Single(create.Graph.Nodes).Type);
    }

    [Fact]
    public void TypeParametersAreEditedAsOneUndoStepAndUsableAsPropertyTypes()
    {
        GraphBuilder box = GraphBuilder.Type("Box");
        var editor = new GraphEditor(box.Graph, Library) { ProjectGraphs = [box.Graph] };

        editor.SetTypeParameters(["T"]);
        GraphProperty content = editor.AddProperty("T");

        Assert.Equal(["T"], box.Graph.TypeParameters);
        Assert.Equal("T", content.Type);
        editor.Undo();
        editor.Undo();
        Assert.Empty(box.Graph.TypeParameters);
        Assert.Throws<ArgumentException>(() => editor.AddProperty("T"));
    }

    [Fact]
    public void NodesOfATypeThatBecameGenericAreReportedAndStillDrawnWithTheirWires()
    {
        GraphBuilder box = GraphBuilder.Type("Box");
        string content = box.Property("Content", "int", "0");
        var program = new GraphBuilder();
        string entry = program.Node(BuiltInNodes.EntryTypeId);
        string made = program.TypeNode(TypeNodes.CreateTypeId, box.Graph);
        string read = program.TypeNode(TypeNodes.GetTypeId, box.Graph, content);
        program.Then(entry, made).Connect(made, "value", read, "object");
        box.Graph.TypeParameters.Add("T");

        GraphCompilationResult result = TypeTests.Compile(program, box);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.NodeId == made && diagnostic.Message.StartsWith("Box is generic", StringComparison.Ordinal));
        var editor = new GraphEditor(program.Graph, Library) { ProjectGraphs = [program.Graph, box.Graph] };
        GraphNode node = program.Graph.FindNode(made)!;
        Assert.Null(editor.DefinitionOf(node));
        NodeDefinition placeholder = editor.PlaceholderFor(node);
        Assert.True(placeholder.FindPin("exec")!.IsExecution);
        Assert.Equal(PinDirection.Output, placeholder.FindPin("value")!.Direction);
        Assert.False(placeholder.FindPin("value")!.IsExecution);
    }
}
