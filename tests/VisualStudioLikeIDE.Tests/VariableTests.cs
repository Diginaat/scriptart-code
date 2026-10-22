using VisualStudioLikeIDE.Compiler;
using VisualStudioLikeIDE.Compiler.Diagnostics;
using VisualStudioLikeIDE.Compiler.Emit;
using VisualStudioLikeIDE.Compiler.Nodes;
using VisualStudioLikeIDE.GraphModel;
using VisualStudioLikeIDE.NodeEditor;

namespace VisualStudioLikeIDE.Tests;

public sealed class VariableTests
{
    internal static string MainBody(GraphBuilder graph, bool expectCleanRoslyn = true)
    {
        GraphCompilationResult result = CodeGenerationTests.Compile(graph);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        GeneratedSource source = result.Source!;
        IReadOnlyList<GraphDiagnostic> roslyn = RoslynVerifier.Verify("Demo", [new VerificationSource("Program.g.cs", source.Text, source)], isExecutable: true);
        Assert.DoesNotContain(roslyn, diagnostic => diagnostic.IsError);
        if (expectCleanRoslyn)
        {
            Assert.Empty(roslyn);
        }

        // The statements between Main's braces, without indentation.
        string[] lines = source.Text.ReplaceLineEndings("\n").Split('\n');
        int open = Array.FindIndex(lines, line => line.Contains("Main()", StringComparison.Ordinal)) + 1;
        int close = Array.FindIndex(lines, open + 1, line => line.Trim() == "}" && line.Length - line.TrimStart().Length == lines[open].Length - lines[open].TrimStart().Length);
        return string.Join("\n", lines[(open + 1)..close].Select(line => line.Trim()));
    }

    private static IEnumerable<GraphDiagnostic> Problems(GraphBuilder graph) => CodeGenerationTests.Compile(graph).Diagnostics;

    [Fact]
    public void VariablesBecomeTypedLocalsAndSetBecomesAssignment()
    {
        var graph = new GraphBuilder();
        string count = graph.Variable("count", "int", "1");
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string set = graph.VariableNode(count, set: true);
        string add = graph.Node("math.int.add");
        string one = graph.Node("literal.int", "1");
        string print = graph.Node("console.write-line");
        string toText = graph.Node("convert.int-to-string");
        graph.Then(entry, set).Then(set, print)
            .Connect(graph.VariableNode(count, set: false), "value", add, "a")
            .Connect(one, "value", add, "b")
            .Connect(add, "result", set, "value")
            .Connect(graph.VariableNode(count, set: false), "value", toText, "value")
            .Connect(toText, "text", print, "text");

        Assert.Equal("""
            int count = 1;
            count = count + 1;
            Console.WriteLine(count.ToString());
            """, MainBody(graph));
    }

    [Fact]
    public void EachUseReadsTheCurrentValue()
    {
        // One Get node feeding prints before and after a Set must not be evaluated once and cached.
        var graph = new GraphBuilder();
        string name = graph.Variable("name", "string", "Ada");
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string get = graph.VariableNode(name, set: false);
        string before = graph.Node("console.write-line");
        string set = graph.VariableNode(name, set: true);
        string after = graph.Node("console.write-line");
        graph.Then(entry, before).Then(before, set).Then(set, after)
            .Connect(get, "value", before, "text")
            .Connect(graph.Node("literal.string", "Grace"), "value", set, "value")
            .Connect(get, "value", after, "text");

        Assert.Equal("""
            string name = "Ada";
            Console.WriteLine(name);
            name = "Grace";
            Console.WriteLine(name);
            """, MainBody(graph));
    }

    [Fact]
    public void VariableNamesWinOverGeneratedLocalNames()
    {
        var graph = new GraphBuilder();
        string line = graph.Variable("line", "string", "");
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string read = graph.Node("console.read-line");
        string set = graph.VariableNode(line, set: true);
        string print = graph.Node("console.write-line");
        graph.Then(entry, read).Then(read, set).Then(set, print)
            .Connect(read, "line", set, "value")
            .Connect(graph.VariableNode(line, set: false), "value", print, "text");

        Assert.Equal("""
            string line = "";
            string line2 = Console.ReadLine() ?? "";
            line = line2;
            Console.WriteLine(line);
            """, MainBody(graph));
    }

    [Fact]
    public void UnusedVariablesAreNotDeclared()
    {
        var graph = new GraphBuilder();
        graph.Variable("unused", "double", "2.5");
        graph.Then(graph.Node(BuiltInNodes.EntryTypeId), graph.PrintLiteral("Hi"));

        Assert.Equal("Console.WriteLine(\"Hi\");", MainBody(graph));
    }

    [Theory]
    [InlineData("class", "int", "0", "does not have a valid C# name")]
    [InlineData("count", "long", "0", "unsupported type 'long'")]
    [InlineData("count", "int", "many", "initial value")]
    public void InvalidVariablesAreErrors(string name, string type, string value, string message)
    {
        var graph = new GraphBuilder();
        graph.Variable(name, type, value);
        graph.Node(BuiltInNodes.EntryTypeId);

        GraphDiagnostic problem = Assert.Single(Problems(graph), diagnostic => diagnostic.Id == DiagnosticIds.InvalidVariable);
        Assert.Contains(message, problem.Message, StringComparison.Ordinal);
        Assert.False(CodeGenerationTests.Compile(graph).Success);
    }

    [Fact]
    public void DuplicateVariableNamesAreAnError()
    {
        var graph = new GraphBuilder();
        graph.Variable("count", "int", "0");
        graph.Variable("count", "string", "");
        graph.Node(BuiltInNodes.EntryTypeId);

        Assert.Contains(Problems(graph), diagnostic => diagnostic.Id == DiagnosticIds.InvalidVariable && diagnostic.Message.Contains("More than one", StringComparison.Ordinal));
    }

    [Fact]
    public void NodeForAMissingVariableIsAnError()
    {
        var graph = new GraphBuilder();
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string set = graph.VariableNode("gone", set: true);
        graph.Then(entry, set);

        GraphDiagnostic problem = Assert.Single(Problems(graph), diagnostic => diagnostic.Id == DiagnosticIds.UnknownVariable);
        Assert.Equal(set, problem.NodeId);
    }

    [Fact]
    public void VariableThatIsOnlySetIsAWarning()
    {
        var graph = new GraphBuilder();
        string total = graph.Variable("total", "int", "0");
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string set = graph.VariableNode(total, set: true);
        graph.Then(entry, set).Connect(graph.Node("literal.int", "5"), "value", set, "value");

        GraphDiagnostic warning = Assert.Single(Problems(graph), diagnostic => diagnostic.Id == DiagnosticIds.VariableNeverRead);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        // Roslyn agrees (CS0219); the graph warning points at the Set node rather than at a line of code.
        Assert.Equal("int total = 0;\ntotal = 5;", MainBody(graph, expectCleanRoslyn: false));
    }

    [Fact]
    public void VariableNodesTakeTheirPinsFromTheVariable()
    {
        var library = NodeLibrary.CreateDefault();
        var graph = new GraphBuilder();
        string score = graph.Variable("score", "double", "0");
        GraphNode node = graph.Graph.FindNode(graph.VariableNode(score, set: true))!;

        NodeDefinition definition = library.Resolve(node, graph.Graph)!;
        Assert.Equal("Set score", definition.DisplayName);
        Assert.Equal(Compiler.TypeSystem.TypeRef.Double, definition.FindPin("value")!.DataType);

        graph.Graph.Variables[0].Type = "string";
        Assert.Equal(Compiler.TypeSystem.TypeRef.String, library.Resolve(node, graph.Graph)!.FindPin("value")!.DataType);
        Assert.Equal(2, library.DefinitionsFor(graph.Graph).Count(candidate => candidate.Category == VariableNodes.Category));
    }

    [Fact]
    public void EditorAddsVariablesWithUniqueNamesAndNodesThatReferToThem()
    {
        var editor = new GraphEditor(GraphDocument.CreateEmpty("Program", GraphKind.EntryPoint), NodeLibrary.CreateDefault());
        GraphVariable first = editor.AddVariable("int");
        GraphVariable second = editor.AddVariable("string");
        Assert.Equal(("variable", "0"), (first.Name, first.Value));
        Assert.Equal(("variable2", ""), (second.Name, second.Value));

        NodeDefinition get = editor.AvailableDefinitions.Single(definition => definition.DisplayName == "Get variable");
        GraphNode node = editor.AddNode(get, 0, 0);
        Assert.Equal(first.Id, node.Properties[VariableNodes.VariableProperty]);

        editor.RenameVariable(first.Id, "count");
        Assert.Equal("Get count", editor.DefinitionOf(node)!.DisplayName);
        editor.Undo();
        Assert.Equal("Get variable", editor.DefinitionOf(node)!.DisplayName);
    }

    [Fact]
    public void ChangingTheTypeDropsWiresThatNoLongerFitInOneUndoStep()
    {
        var editor = new GraphEditor(GraphDocument.CreateEmpty("Program", GraphKind.EntryPoint), NodeLibrary.CreateDefault());
        GraphVariable variable = editor.AddVariable("string");
        editor.SetVariableValue(variable.Id, "hello");
        GraphNode get = editor.AddNode(editor.AvailableDefinitions.Single(definition => definition.DisplayName == "Get variable"), 0, 0);
        GraphNode print = editor.AddNode("console.write-line", 300, 0);
        editor.Connect(new PinRef(get.Id, "value"), new PinRef(print.Id, "text"));

        editor.SetVariableType(variable.Id, "int");
        Assert.Empty(editor.Graph.Connections);
        Assert.Equal(("int", "0"), (variable.Type, variable.Value));

        editor.Undo();
        Assert.Single(editor.Graph.Connections);
        Assert.Equal(("string", "hello"), (variable.Type, variable.Value));
    }

    [Fact]
    public void DeletingAVariableRemovesItsNodesAndUndoRestoresThem()
    {
        var editor = new GraphEditor(GraphDocument.CreateEmpty("Program", GraphKind.EntryPoint), NodeLibrary.CreateDefault());
        GraphVariable variable = editor.AddVariable("bool");
        GraphNode get = editor.AddNode(editor.AvailableDefinitions.Single(definition => definition.DisplayName == "Get variable"), 0, 0);
        GraphNode not = editor.AddNode("logic.not", 300, 0);
        editor.Connect(new PinRef(get.Id, "value"), new PinRef(not.Id, "value"));

        editor.DeleteVariable(variable.Id);
        Assert.Empty(editor.Graph.Variables);
        Assert.Equal([not], editor.Graph.Nodes);
        Assert.Empty(editor.Graph.Connections);

        editor.Undo();
        Assert.Equal([variable], editor.Graph.Variables);
        Assert.Equal(2, editor.Graph.Nodes.Count);
        Assert.Single(editor.Graph.Connections);
    }

    [Fact]
    public void VariablesRoundTripAndOlderFilesGetAnEmptyList()
    {
        var graph = new GraphBuilder();
        graph.Variable("count", "int", "3");
        GraphDocument loaded = GraphSerializer.Deserialize(GraphSerializer.Serialize(graph.Graph));
        GraphVariable variable = Assert.Single(loaded.Variables);
        Assert.Equal(("count", "int", "3"), (variable.Name, variable.Type, variable.Value));

        GraphDocument old = GraphSerializer.Deserialize("""{ "schemaVersion": 3, "id": "g", "name": "P", "nodes": [], "connections": [], "comments": [] }""");
        Assert.Empty(old.Variables);
        Assert.Equal(GraphDocument.CurrentSchemaVersion, old.SchemaVersion);
    }
}
