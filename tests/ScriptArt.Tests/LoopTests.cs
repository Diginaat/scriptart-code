using ScriptArt.Compiler.Diagnostics;
using ScriptArt.Compiler.Nodes;

namespace ScriptArt.Tests;

public sealed class LoopTests
{
    private static string MainBody(GraphBuilder graph) => VariableTests.MainBody(graph);

    private static GraphDiagnostic SingleError(GraphBuilder graph, string id)
    {
        GraphDiagnostic error = Assert.Single(CodeGenerationTests.Compile(graph).Diagnostics, diagnostic => diagnostic.IsError);
        Assert.Equal(id, error.Id);
        return error;
    }

    [Fact]
    public void WhileLoopRereadsItsConditionAndContinuesWithCompleted()
    {
        var graph = new GraphBuilder();
        string count = graph.Variable("count", "int", "0");
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string loop = graph.Node("flow.while");
        string less = graph.Node("compare.int.less");
        string print = graph.Node("console.write-line");
        string toText = graph.Node("convert.int-to-string");
        string increment = graph.VariableNode(count, set: true);
        string add = graph.Node("math.int.add");
        string done = graph.PrintLiteral("done");
        graph.Then(entry, loop).Then(loop, print, "body").Then(print, increment).Then(loop, done, "completed")
            .Connect(graph.VariableNode(count, set: false), "value", less, "a")
            .Connect(graph.Node("literal.int", "3"), "value", less, "b")
            .Connect(less, "result", loop, "condition")
            .Connect(graph.VariableNode(count, set: false), "value", toText, "value")
            .Connect(toText, "text", print, "text")
            .Connect(graph.VariableNode(count, set: false), "value", add, "a")
            .Connect(graph.Node("literal.int", "1"), "value", add, "b")
            .Connect(add, "result", increment, "value");

        Assert.Equal("""
            int count = 0;
            while (count < 3)
            {
            Console.WriteLine(count.ToString());
            count = count + 1;
            }

            Console.WriteLine("done");
            """, MainBody(graph));
    }

    [Fact]
    public void ForLoopIndexIsAvailableInsideTheBody()
    {
        var graph = new GraphBuilder();
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string loop = graph.Node("flow.for");
        string print = graph.Node("console.write-line");
        string toText = graph.Node("convert.int-to-string");
        graph.Then(entry, loop).Then(loop, print, "body")
            .Connect(graph.Node("literal.int", "0"), "value", loop, "start")
            .Connect(graph.Node("literal.int", "3"), "value", loop, "end")
            .Connect(loop, "index", toText, "value")
            .Connect(toText, "text", print, "text");

        Assert.Equal("""
            for (int i = 0; i < 3; i++)
            {
            Console.WriteLine(i.ToString());
            }
            """, MainBody(graph));
    }

    [Fact]
    public void NestedForLoopsGetDistinctIndexNames()
    {
        var graph = new GraphBuilder();
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string outer = graph.Node("flow.for");
        string inner = graph.Node("flow.for");
        string print = graph.Node("console.write-line");
        string toText = graph.Node("convert.int-to-string");
        string multiply = graph.Node("math.int.multiply");
        graph.Then(entry, outer).Then(outer, inner, "body").Then(inner, print, "body")
            .Connect(graph.Node("literal.int", "1"), "value", outer, "start")
            .Connect(graph.Node("literal.int", "3"), "value", outer, "end")
            .Connect(graph.Node("literal.int", "1"), "value", inner, "start")
            .Connect(outer, "index", inner, "end")
            .Connect(outer, "index", multiply, "a")
            .Connect(inner, "index", multiply, "b")
            .Connect(multiply, "result", toText, "value")
            .Connect(toText, "text", print, "text");

        Assert.Equal("""
            for (int i = 1; i < 3; i++)
            {
            for (int i2 = 1; i2 < i; i2++)
            {
            Console.WriteLine((i * i2).ToString());
            }
            }
            """, MainBody(graph));
    }

    [Fact]
    public void BreakInABranchEndsTheLoop()
    {
        var graph = new GraphBuilder();
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string loop = graph.Node("flow.while");
        string read = graph.Node("console.read-line");
        string branch = graph.Node("core.branch");
        string equals = graph.Node("string.equals");
        string stop = graph.Node("flow.break");
        string print = graph.Node("console.write-line");
        graph.Then(entry, loop).Then(loop, read, "body").Then(read, branch).Then(branch, stop, "true").Then(branch, print, "false")
            .Connect(graph.Node("literal.bool", "true"), "value", loop, "condition")
            .Connect(read, "line", equals, "a")
            .Connect(graph.Node("literal.string", ""), "value", equals, "b")
            .Connect(equals, "result", branch, "condition")
            .Connect(read, "line", print, "text");

        Assert.Equal("""
            while (true)
            {
            string line = Console.ReadLine() ?? "";
            if (line == "")
            {
            break;
            }

            Console.WriteLine(line);
            }
            """, MainBody(graph));
    }

    [Fact]
    public void ContinueAtTheEndOfTheBodyIsLeftOut()
    {
        var graph = new GraphBuilder();
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string loop = graph.Node("flow.for");
        string print = graph.PrintLiteral("x");
        string next = graph.Node("flow.continue");
        graph.Then(entry, loop).Then(loop, print, "body").Then(print, next)
            .Connect(graph.Node("literal.int", "0"), "value", loop, "start")
            .Connect(graph.Node("literal.int", "2"), "value", loop, "end");

        Assert.Equal("""
            for (int i = 0; i < 2; i++)
            {
            Console.WriteLine("x");
            }
            """, MainBody(graph));
    }

    [Theory]
    [InlineData("flow.break")]
    [InlineData("flow.continue")]
    public void JumpOutsideALoopIsAnError(string type)
    {
        var graph = new GraphBuilder();
        string jump = graph.Node(type);
        graph.Then(graph.Node(BuiltInNodes.EntryTypeId), jump);

        Assert.Equal(jump, SingleError(graph, DiagnosticIds.JumpOutsideLoop).NodeId);
    }

    [Fact]
    public void IndexIsNotAvailableAfterTheLoop()
    {
        var graph = new GraphBuilder();
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string loop = graph.Node("flow.for");
        string print = graph.Node("console.write-line");
        string toText = graph.Node("convert.int-to-string");
        graph.Then(entry, loop).Then(loop, print, "completed")
            .Connect(graph.Node("literal.int", "0"), "value", loop, "start")
            .Connect(graph.Node("literal.int", "2"), "value", loop, "end")
            .Connect(loop, "index", toText, "value")
            .Connect(toText, "text", print, "text");

        SingleError(graph, DiagnosticIds.ValueNotAvailable);
    }

    [Fact]
    public void WiringTheBodyBackToTheLoopIsACycle()
    {
        var graph = new GraphBuilder();
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string loop = graph.Node("flow.while");
        string print = graph.PrintLiteral("x");
        graph.Then(entry, loop).Then(loop, print, "body").Then(print, loop)
            .Connect(graph.Node("literal.bool", "true"), "value", loop, "condition");

        SingleError(graph, DiagnosticIds.ExecutionCycle);
    }

    [Fact]
    public void BodyThatRunsIntoTheCodeAfterTheLoopIsUnstructured()
    {
        var graph = new GraphBuilder();
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string loop = graph.Node("flow.while");
        string inside = graph.PrintLiteral("inside");
        string after = graph.PrintLiteral("after");
        graph.Then(entry, loop).Then(loop, inside, "body").Then(inside, after).Then(loop, after, "completed")
            .Connect(graph.Node("literal.bool", "false"), "value", loop, "condition");

        SingleError(graph, DiagnosticIds.UnstructuredFlow);
    }
}
