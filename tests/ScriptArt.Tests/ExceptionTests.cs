using ScriptArt.Compiler;
using ScriptArt.Compiler.Diagnostics;
using ScriptArt.Compiler.Nodes;

namespace ScriptArt.Tests;

public sealed class ExceptionTests
{
    private static string MainBody(GraphBuilder graph) => VariableTests.MainBody(graph);

    [Fact]
    public void TryCatchesParseErrorsAndReportsTheMessage()
    {
        var graph = new GraphBuilder();
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string attempt = graph.Node("flow.try");
        string parse = graph.Node("convert.string-to-int");
        string toText = graph.Node("convert.int-to-string");
        string print = graph.Node("console.write-line");
        string report = graph.Node("console.write-line");
        string done = graph.PrintLiteral("done");
        graph.Then(entry, attempt).Then(attempt, print, "try").Then(attempt, report, "catch").Then(attempt, done, "completed")
            .Connect(graph.Node("literal.string", "x"), "value", parse, "text")
            .Connect(parse, "value", toText, "value")
            .Connect(toText, "text", print, "text")
            .Connect(attempt, "message", report, "text");

        Assert.Equal("""
            try
            {
            Console.WriteLine(int.Parse("x").ToString());
            }
            catch (Exception exception)
            {
            string message = exception.Message;
            Console.WriteLine(message);
            }

            Console.WriteLine("done");
            """, MainBody(graph));
    }

    [Fact]
    public void FinallyIsGeneratedOnlyWhenConnectedAndAnUnusedMessageLeavesTheExceptionUnnamed()
    {
        var graph = new GraphBuilder();
        string attempt = graph.Node("flow.try");
        string fail = graph.Node("flow.throw");
        graph.Then(graph.Node(BuiltInNodes.EntryTypeId), attempt).Then(attempt, fail, "try").Then(attempt, graph.PrintLiteral("cleanup"), "finally")
            .Connect(graph.Node("literal.string", "boom"), "value", fail, "message");

        Assert.Equal("""
            try
            {
            throw new InvalidOperationException("boom");
            }
            catch (Exception)
            {
            }
            finally
            {
            Console.WriteLine("cleanup");
            }
            """, MainBody(graph));
    }

    [Fact]
    public void FunctionThatReturnsInTryAndCatchReturnsOnEveryPath()
    {
        var function = GraphBuilder.Function("SafeParse", "int");
        string text = function.Parameter("text", "string");
        string entry = function.Node(FunctionNodes.EntryTypeId);
        string attempt = function.Node("flow.try");
        string parse = function.Node("convert.string-to-int");
        string parsed = function.Node(FunctionNodes.ReturnTypeId);
        string fallback = function.Node(FunctionNodes.ReturnTypeId);
        function.Then(entry, attempt).Then(attempt, parsed, "try").Then(attempt, fallback, "catch")
            .Connect(entry, text, parse, "text")
            .Connect(parse, "value", parsed, "value")
            .Connect(function.Node("literal.int", "0"), "value", fallback, "value");

        GraphCompilationResult result = CodeGenerationTests.Compile(function);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("catch (Exception)\n        {\n            return 0;\n        }\n    }", result.Source!.Text.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void ReturnAndBreakCannotLeaveAFinallyBlock()
    {
        var graph = new GraphBuilder();
        string loop = graph.Node("flow.while");
        string attempt = graph.Node("flow.try");
        string leave = graph.Node("flow.break");
        string stop = graph.Node("core.return");
        string inner = graph.Node("flow.try");
        graph.Then(graph.Node(BuiltInNodes.EntryTypeId), loop).Then(loop, attempt, "body").Then(attempt, inner, "finally")
            .Then(inner, leave, "try").Then(inner, stop, "finally")
            .Connect(graph.Node("literal.bool", "true"), "value", loop, "condition");

        GraphDiagnostic[] errors = [.. CodeGenerationTests.Compile(graph).Diagnostics.Where(diagnostic => diagnostic.IsError)];

        Assert.Equal([(DiagnosticIds.JumpOutOfFinally, leave), (DiagnosticIds.JumpOutOfFinally, stop)],
            errors.Select(error => (error.Id, error.NodeId)).OrderBy(error => error.NodeId == stop));
    }

    [Fact]
    public void BreakInATryInsideALoopIsAllowed()
    {
        var graph = new GraphBuilder();
        string loop = graph.Node("flow.while");
        string attempt = graph.Node("flow.try");
        graph.Then(graph.Node(BuiltInNodes.EntryTypeId), loop).Then(loop, attempt, "body").Then(attempt, graph.Node("flow.break"), "try")
            .Connect(graph.Node("literal.bool", "true"), "value", loop, "condition");

        Assert.Contains("try\n{\nbreak;\n}", MainBody(graph), StringComparison.Ordinal);
    }
}
