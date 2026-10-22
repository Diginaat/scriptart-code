using VisualStudioLikeIDE.Compiler;
using VisualStudioLikeIDE.Compiler.Diagnostics;
using VisualStudioLikeIDE.Compiler.Emit;
using VisualStudioLikeIDE.Compiler.Nodes;
using VisualStudioLikeIDE.GraphModel;
using VisualStudioLikeIDE.NodeEditor;

namespace VisualStudioLikeIDE.Tests;

public sealed class FunctionTests
{
    private static readonly GraphCompiler Compiler = new(NodeLibrary.CreateDefault());

    private static GraphCompilationResult Compile(GraphBuilder graph, params GraphBuilder[] functions) =>
        Compiler.Compile(graph.Graph, new GraphCompilationOptions("Demo", [.. functions.Select(function => function.Graph)]), "Graphs/Test.graph.json");

    /// <summary>Compiles the program and its functions, checks Roslyn accepts them together, and returns each file's text.</summary>
    private static string[] CompileProject(GraphBuilder program, params GraphBuilder[] functions)
    {
        GeneratedSource[] sources = [.. new[] { program }.Concat(functions).Select(graph =>
        {
            GraphCompilationResult result = Compile(graph, functions);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            return result.Source!;
        })];
        IReadOnlyList<GraphDiagnostic> roslyn = RoslynVerifier.Verify("Demo",
            [.. sources.Select(source => new VerificationSource(source.RelativePath, source.Text, source))], isExecutable: true);
        Assert.Empty(roslyn);
        return [.. sources.Select(source => source.Text)];
    }

    /// <summary>The generated text after the header and namespace: the class declaration and its members.</summary>
    private static string ClassText(string source) => source[source.IndexOf("internal static", StringComparison.Ordinal)..].TrimEnd();

    private static GraphBuilder Square()
    {
        var square = GraphBuilder.Function("Square", "int");
        string value = square.Parameter("value", "int");
        string entry = square.Node(FunctionNodes.EntryTypeId);
        string multiply = square.Node("math.int.multiply");
        string result = square.Node(FunctionNodes.ReturnTypeId);
        square.Then(entry, result)
            .Connect(entry, value, multiply, "a")
            .Connect(entry, value, multiply, "b")
            .Connect(multiply, "result", result, "value");
        return square;
    }

    [Fact]
    public void FunctionBecomesAPrivateMethodOfTheProgramClassAndCallsUseItsResult()
    {
        GraphBuilder square = Square();
        var program = new GraphBuilder();
        string entry = program.Node(BuiltInNodes.EntryTypeId);
        string call = program.Call(square.Graph);
        string print = program.Node("console.write-line");
        string toText = program.Node("convert.int-to-string");
        program.Then(entry, call).Then(call, print)
            .Connect(program.Node("literal.int", "7"), "value", call, square.Graph.Parameters[0].Id)
            .Connect(call, "result", toText, "value")
            .Connect(toText, "text", print, "text");

        string[] sources = CompileProject(program, square);

        Assert.Equal("""
            internal static partial class Program
            {
                public static void Main()
                {
                    int square = Square(7);
                    Console.WriteLine(square.ToString());
                }
            }
            """, ClassText(sources[0]).ReplaceLineEndings("\n"));
        Assert.Equal("""
            internal static partial class Program
            {
                private static int Square(int value)
                {
                    return value * value;
                }
            }
            """, ClassText(sources[1]).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void VoidFunctionsAndUnusedResultsAreCalledAsStatements()
    {
        GraphBuilder square = Square();
        var greet = GraphBuilder.Function("Greet");
        string name = greet.Parameter("name", "string");
        string greetEntry = greet.Node(FunctionNodes.EntryTypeId);
        string print = greet.Node("console.write-line");
        string concat = greet.Node("string.concat");
        greet.Then(greetEntry, print)
            .Connect(greet.Node("literal.string", "Hello, "), "value", concat, "a")
            .Connect(greetEntry, name, concat, "b")
            .Connect(concat, "result", print, "text");

        var program = new GraphBuilder();
        string entry = program.Node(BuiltInNodes.EntryTypeId);
        string callGreet = program.Call(greet.Graph);
        string callSquare = program.Call(square.Graph);
        program.Then(entry, callGreet).Then(callGreet, callSquare)
            .Connect(program.Node("literal.string", "Ada"), "value", callGreet, name)
            .Connect(program.Node("literal.int", "3"), "value", callSquare, square.Graph.Parameters[0].Id);

        string[] sources = CompileProject(program, square, greet);

        Assert.Contains("        Greet(\"Ada\");\n        Square(3);\n", sources[0], StringComparison.Ordinal);
        Assert.Contains("    private static void Greet(string name)\n    {\n        Console.WriteLine(\"Hello, \" + name);\n    }", sources[2], StringComparison.Ordinal);
    }

    [Fact]
    public void RecursiveFunctionReturnsOnEveryPath()
    {
        var factorial = GraphBuilder.Function("Factorial", "int");
        string n = factorial.Parameter("n", "int");
        string entry = factorial.Node(FunctionNodes.EntryTypeId);
        string branch = factorial.Node("core.branch");
        string atMostOne = factorial.Node("compare.int.less-or-equal");
        string returnOne = factorial.Node(FunctionNodes.ReturnTypeId);
        string recurse = factorial.Call(factorial.Graph);
        string minus = factorial.Node("math.int.subtract");
        string times = factorial.Node("math.int.multiply");
        string returnProduct = factorial.Node(FunctionNodes.ReturnTypeId);
        factorial.Then(entry, branch).Then(branch, returnOne, "true").Then(branch, recurse, "false").Then(recurse, returnProduct)
            .Connect(entry, n, atMostOne, "a")
            .Connect(factorial.Node("literal.int", "1"), "value", atMostOne, "b")
            .Connect(atMostOne, "result", branch, "condition")
            .Connect(factorial.Node("literal.int", "1"), "value", returnOne, "value")
            .Connect(entry, n, minus, "a")
            .Connect(factorial.Node("literal.int", "1"), "value", minus, "b")
            .Connect(minus, "result", recurse, n)
            .Connect(entry, n, times, "a")
            .Connect(recurse, "result", times, "b")
            .Connect(times, "result", returnProduct, "value");
        var program = new GraphBuilder();
        program.Then(program.Node(BuiltInNodes.EntryTypeId), program.PrintLiteral("x"));

        string[] sources = CompileProject(program, factorial);

        Assert.Equal("""
            internal static partial class Program
            {
                private static int Factorial(int n)
                {
                    if (n <= 1)
                    {
                        return 1;
                    }

                    int factorial = Factorial(n - 1);
                    return n * factorial;
                }
            }
            """, ClassText(sources[1]).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void FunctionWithAPathThatDoesNotReturnIsAnError()
    {
        var positive = GraphBuilder.Function("Positive", "int");
        string value = positive.Parameter("value", "int");
        string entry = positive.Node(FunctionNodes.EntryTypeId);
        string branch = positive.Node("core.branch");
        string greater = positive.Node("compare.int.greater");
        string result = positive.Node(FunctionNodes.ReturnTypeId);
        positive.Then(entry, branch).Then(branch, result, "true")
            .Connect(entry, value, greater, "a")
            .Connect(positive.Node("literal.int", "0"), "value", greater, "b")
            .Connect(greater, "result", branch, "condition")
            .Connect(entry, value, result, "value");

        GraphDiagnostic error = Assert.Single(Compile(positive, positive).Diagnostics, diagnostic => diagnostic.IsError);
        Assert.Equal((DiagnosticIds.MissingReturn, entry), (error.Id, error.NodeId));
    }

    [Fact]
    public void ReturnWithoutAValueInAFunctionThatReturnsOneIsAnError()
    {
        var function = GraphBuilder.Function("Answer", "int");
        string stop = function.Node("core.return");
        function.Then(function.Node(FunctionNodes.EntryTypeId), stop);

        Assert.Contains(Compile(function, function).Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.MissingReturn && diagnostic.NodeId == stop);
    }

    [Fact]
    public void FunctionNodesThatDoNotFitTheGraphAreErrors()
    {
        var program = new GraphBuilder();
        string entry = program.Node(BuiltInNodes.EntryTypeId);
        string call = program.Call(new GraphDocument { Id = "gone", Name = "Gone", Kind = GraphKind.Function });
        program.Then(entry, call);
        string stray = program.Node(FunctionNodes.EntryTypeId);

        GraphDiagnostic[] errors = [.. Compile(program).Diagnostics.Where(diagnostic => diagnostic.IsError)];
        Assert.Equal([call, stray], errors.Where(error => error.Id == DiagnosticIds.UnknownFunction).Select(error => error.NodeId));
    }

    [Theory]
    [InlineData("Program", "value", "cannot be named 'Program'")]
    [InlineData("class", "value", "not a valid C# method name")]
    [InlineData("Twice", "count", "same name as a variable")]
    [InlineData("Twice", "1st", "does not have a valid C# name")]
    public void InvalidSignaturesAreErrors(string name, string parameterName, string message)
    {
        var function = GraphBuilder.Function(name);
        function.Parameter(parameterName, "int");
        function.Variable("count", "int", "0");
        function.Node(FunctionNodes.EntryTypeId);

        Assert.Contains(Compile(function, function).Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.InvalidFunction && diagnostic.Message.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void CallNodesFollowTheFunctionSignature()
    {
        GraphBuilder square = Square();
        var program = new GraphBuilder();
        var editor = new GraphEditor(program.Graph, Compiler.Library) { Functions = [square.Graph] };
        NodeDefinition callDefinition = editor.AvailableDefinitions.Single(definition => definition.DisplayName == "Call Square");
        GraphNode call = editor.AddNode(callDefinition, 0, 0);
        Assert.Equal(square.Graph.Id, call.Properties[FunctionNodes.FunctionProperty]);

        square.Graph.Name = "Squared";
        square.Graph.Parameters[0].Name = "number";
        NodeDefinition renamed = editor.DefinitionOf(call)!;
        Assert.Equal("Call Squared", renamed.DisplayName);
        Assert.Equal("number", renamed.FindPin(square.Graph.Parameters[0].Id)!.DisplayName);
    }

    [Fact]
    public void ChangingTheSignatureDropsWiresThatNoLongerFitInOneUndoStep()
    {
        var function = GraphBuilder.Function("Show");
        var editor = new GraphEditor(function.Graph, Compiler.Library) { Functions = [function.Graph] };
        GraphParameter parameter = editor.AddParameter("string");
        GraphNode entry = editor.AddNode(FunctionNodes.EntryTypeId, 0, 0);
        GraphNode print = editor.AddNode("console.write-line", 300, 0);
        editor.Connect(new PinRef(entry.Id, parameter.Id), new PinRef(print.Id, "text"));
        editor.Connect(new PinRef(entry.Id, "then"), new PinRef(print.Id, "exec"));

        editor.SetParameterType(parameter.Id, "int");
        Assert.Single(editor.Graph.Connections);
        editor.Undo();
        Assert.Equal(2, editor.Graph.Connections.Count);

        editor.DeleteParameter(parameter.Id);
        Assert.Empty(editor.Graph.Parameters);
        Assert.Single(editor.Graph.Connections);
        editor.Undo();
        Assert.Equal([parameter], editor.Graph.Parameters);
        Assert.Equal(2, editor.Graph.Connections.Count);

        editor.SetReturnType("int");
        Assert.Contains(editor.AvailableDefinitions, definition => definition.TypeId == FunctionNodes.ReturnTypeId);
    }

    [Fact]
    public void WiresToADeletedParameterOnCallNodesInOtherGraphsAreReported()
    {
        GraphBuilder square = Square();
        var program = new GraphBuilder();
        string call = program.Call(square.Graph);
        program.Then(program.Node(BuiltInNodes.EntryTypeId), call)
            .Connect(program.Node("literal.int", "7"), "value", call, square.Graph.Parameters[0].Id);
        square.Graph.Parameters.Clear();

        GraphCompilationResult result = Compile(program, square);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.IsError && diagnostic.NodeId == call);
    }

    [Fact]
    public void SignatureRoundTripsAndOlderFilesHaveNoParameters()
    {
        GraphDocument loaded = GraphSerializer.Deserialize(GraphSerializer.Serialize(Square().Graph));
        Assert.Equal((GraphKind.Function, "int"), (loaded.Kind, loaded.ReturnType));
        Assert.Equal(("value", "int"), (loaded.Parameters[0].Name, loaded.Parameters[0].Type));

        GraphDocument old = GraphSerializer.Deserialize("""{ "schemaVersion": 4, "id": "g", "name": "P", "nodes": [], "connections": [], "comments": [], "variables": [] }""");
        Assert.Empty(old.Parameters);
        Assert.Null(old.ReturnType);

        GraphDocument sparse = GraphSerializer.Deserialize("""{ "schemaVersion": 5, "id": "g", "name": "P" }""");
        Assert.True(sparse.Nodes is [] && sparse.Connections is [] && sparse.Comments is [] && sparse.Variables is [] && sparse.Parameters is []);
    }
}
