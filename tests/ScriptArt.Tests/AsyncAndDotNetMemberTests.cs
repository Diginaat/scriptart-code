using ScriptArt.Compiler;
using ScriptArt.Compiler.Diagnostics;
using ScriptArt.Compiler.Nodes;
using ScriptArt.GraphModel;

namespace ScriptArt.Tests;

/// <summary>Typed catch clauses and rethrow, constructors, properties, and generic methods of .NET types, and async/await.</summary>
public sealed class AsyncAndDotNetMemberTests
{
    private static ExternalMethod Framework(string typeId) =>
        Assert.Single(ExternalApi.Framework, method => method.TypeId == typeId);

    [Fact]
    public void TryCatchesTheChosenExceptionTypeAndRethrowsFromCatch()
    {
        var graph = new GraphBuilder();
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string attempt = graph.With(graph.Node("flow.try"), "exception", "FormatException");
        string parse = graph.Node("convert.string-to-int");
        string print = graph.Node("console.write-line");
        string text = graph.Node("convert.int-to-string");
        string log = graph.Node("console.write-line");
        string rethrow = graph.Node("flow.rethrow");
        graph.Then(entry, attempt).Then(attempt, print, "try").Then(attempt, log, "catch").Then(log, rethrow)
            .Connect(graph.Node("literal.string", "x"), "value", parse, "text")
            .Connect(parse, "value", text, "value")
            .Connect(text, "text", print, "text")
            .Connect(attempt, "message", log, "text");

        Assert.Equal("""
            try
            {
            Console.WriteLine(int.Parse("x").ToString());
            }
            catch (FormatException exception)
            {
            string message = exception.Message;
            Console.WriteLine(message);
            throw;
            }
            """, VariableTests.MainBody(graph));
    }

    [Fact]
    public void ThrowUsesTheChosenTypeAndRethrowOutsideCatchIsReported()
    {
        var graph = new GraphBuilder();
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string attempt = graph.Node("flow.try");
        string thrown = graph.With(graph.Node("flow.throw"), "exception", "ArgumentException");
        graph.Then(entry, attempt).Then(attempt, thrown, "try").Then(attempt, graph.Node("flow.rethrow"), "finally")
            .Connect(graph.Node("literal.string", "bad"), "value", thrown, "message");

        GraphCompilationResult result = CodeGenerationTests.Compile(graph);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.RethrowOutsideCatch);

        var valid = new GraphBuilder();
        string start = valid.Node(BuiltInNodes.EntryTypeId);
        string throwing = valid.With(valid.Node("flow.throw"), "exception", "ArgumentException");
        valid.Then(start, throwing).Connect(valid.Node("literal.string", "bad"), "value", throwing, "message");
        Assert.Equal("throw new ArgumentException(\"bad\");", VariableTests.MainBody(valid));
    }

    [Fact]
    public void DotNetConstructorsPropertiesAndInstanceMethodsOfValueTypesAreOffered()
    {
        var graph = new GraphBuilder();
        string builder = graph.Variable("builder", "System.Text.StringBuilder", "");
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string append = graph.Node(Framework("dotnet:M:System.Text.StringBuilder.Append(System.String)").TypeId);
        string setLength = graph.Node(Framework("dotnet:set:P:System.Text.StringBuilder.Length").TypeId);
        string print = graph.Node("console.write-line");
        string length = graph.Node(Framework("dotnet:P:System.Text.StringBuilder.Length").TypeId);
        string text = graph.Node("convert.int-to-string");
        string span = graph.Node(Framework("dotnet:M:System.TimeSpan.#ctor(System.Int32,System.Int32,System.Int32)").TypeId);
        string seconds = graph.Node(Framework("dotnet:P:System.TimeSpan.TotalSeconds").TypeId);
        string printSeconds = graph.Node("console.write-line");
        string secondsText = graph.Node("convert.double-to-string");
        graph.Then(entry, append).Then(append, setLength).Then(setLength, print).Then(print, printSeconds)
            .Connect(graph.VariableNode(builder, set: false), "value", append, "instance")
            .Connect(graph.Node("literal.string", "abc"), "value", append, "value")
            .Connect(graph.VariableNode(builder, set: false), "value", setLength, "instance")
            .Connect(graph.Node("literal.int", "2"), "value", setLength, "value")
            .Connect(graph.VariableNode(builder, set: false), "value", length, "instance")
            .Connect(length, "result", text, "value")
            .Connect(text, "text", print, "text")
            .Connect(graph.Node("literal.int", "0"), "value", span, "hours")
            .Connect(graph.Node("literal.int", "1"), "value", span, "minutes")
            .Connect(graph.Node("literal.int", "5"), "value", span, "seconds")
            .Connect(span, "result", seconds, "instance")
            .Connect(seconds, "result", secondsText, "value")
            .Connect(secondsText, "text", printSeconds, "text");

        Assert.Equal("""
            var builder = new StringBuilder();
            builder.Append("abc");
            builder.Length = 2;
            Console.WriteLine(builder.Length.ToString());
            Console.WriteLine(new TimeSpan(0, 1, 5).TotalSeconds.ToString());
            """, VariableTests.MainBody(graph));
        Assert.True(Framework("dotnet:P:System.DateTime.Now").IsStatic);
    }

    [Fact]
    public void GenericMethodTakesItsTypeArgumentsFromTheNode()
    {
        ExternalMethod first = Assert.Single(ExternalApi.Framework, method => method.DocumentationId.StartsWith("M:System.Linq.Enumerable.First``1(System.Collections.Generic.IEnumerable{``0})", StringComparison.Ordinal));
        var graph = new GraphBuilder();
        string words = graph.Variable("words", "List<string>", "");
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string add = graph.Node("list.string.add");
        string pick = graph.With(graph.Node(first.TypeId), ExternalApi.TypeArgumentsProperty, "string");
        string print = graph.Node("console.write-line");
        graph.Then(entry, add).Then(add, pick).Then(pick, print)
            .Connect(graph.VariableNode(words, set: false), "value", add, "list")
            .Connect(graph.Node("literal.string", "hi"), "value", add, "item")
            .Connect(graph.VariableNode(words, set: false), "value", pick, "source")
            .Connect(pick, "result", print, "text");

        Assert.Equal("""
            var words = new List<string>();
            words.Add("hi");
            string first = Enumerable.First<string>(words);
            Console.WriteLine(first);
            """, VariableTests.MainBody(graph));

        graph.With(pick, ExternalApi.TypeArgumentsProperty, "nothing");
        Assert.Contains(CodeGenerationTests.Compile(graph).Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UnknownType && diagnostic.NodeId == pick);
    }

    [Fact]
    public void AwaitingMakesTheFunctionAndItsCallersAsync()
    {
        var wait = GraphBuilder.Function("Wait");
        string start = wait.Node(FunctionNodes.EntryTypeId);
        string delay = wait.Node(Framework("dotnet:M:System.Threading.Tasks.Task.Delay(System.Int32)").TypeId);
        wait.Then(start, delay).Connect(wait.Node("literal.int", "10"), "value", delay, "millisecondsDelay");
        var program = new GraphBuilder();
        string entry = program.Node(BuiltInNodes.EntryTypeId);
        string call = program.Call(wait.Graph);
        program.Then(entry, call).Then(call, program.PrintLiteral("done"));

        string[] sources = TypeTests.CompileProject(program, wait);

        Assert.Contains("public static async Task Main()", sources[0], StringComparison.Ordinal);
        Assert.Contains("await Wait();", sources[0], StringComparison.Ordinal);
        Assert.Contains("public static async Task Wait()", sources[1], StringComparison.Ordinal);
        Assert.Contains("await Task.Delay(10);", sources[1], StringComparison.Ordinal);
    }

    [Fact]
    public void AsyncMethodResultIsAwaitedAndOptionalParametersAreLeftOut()
    {
        ExternalMethod read = Framework("dotnet:M:System.IO.File.ReadAllTextAsync(System.String,System.Threading.CancellationToken)");
        Assert.True(read.IsAsync);
        var graph = new GraphBuilder();
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string node = graph.Node(read.TypeId);
        string print = graph.Node("console.write-line");
        graph.Then(entry, node).Then(node, print)
            .Connect(graph.Node("literal.string", "a.txt"), "value", node, "path")
            .Connect(node, "result", print, "text");

        Assert.Equal("""
            string readAllTextAsync = await File.ReadAllTextAsync("a.txt");
            Console.WriteLine(readAllTextAsync);
            """, VariableTests.MainBody(graph));
    }
}
