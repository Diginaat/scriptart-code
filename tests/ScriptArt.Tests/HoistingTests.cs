using ScriptArt.Compiler.Nodes;

namespace ScriptArt.Tests;

public sealed class HoistingTests
{
    private static string MainBody(GraphBuilder graph) => VariableTests.MainBody(graph);

    [Fact]
    public void ValueUsedTwiceByOneStatementIsComputedOnceIntoALocal()
    {
        var graph = new GraphBuilder();
        string read = graph.Node("console.read-line");
        string join = graph.Node("string.concat");
        string upper = graph.Node("string.concat");
        string print = graph.Node("console.write-line");
        graph.Then(graph.Node(BuiltInNodes.EntryTypeId), read).Then(read, print)
            .Connect(read, "line", upper, "a")
            .Connect(graph.Node("literal.string", "!"), "value", upper, "b")
            .Connect(upper, "result", join, "a")
            .Connect(upper, "result", join, "b")
            .Connect(join, "result", print, "text");

        Assert.Equal("""
            string line = Console.ReadLine() ?? "";
            string result = line + "!";
            Console.WriteLine(result + result);
            """, MainBody(graph));
    }

    [Fact]
    public void LiteralsAndValuesUsedByDifferentStatementsStayInline()
    {
        var graph = new GraphBuilder();
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string text = graph.Node("literal.string", "hi");
        string twice = graph.Node("string.concat");
        string first = graph.Node("console.write-line");
        string second = graph.Node("console.write-line");
        graph.Then(entry, first).Then(first, second)
            .Connect(text, "value", twice, "a")
            .Connect(text, "value", twice, "b")
            .Connect(twice, "result", first, "text")
            .Connect(twice, "result", second, "text");

        Assert.Equal("""
            Console.WriteLine("hi" + "hi");
            Console.WriteLine("hi" + "hi");
            """, MainBody(graph));
    }

    [Fact]
    public void LoopConditionsAreNotHoistedBecauseTheyAreReevaluated()
    {
        var graph = new GraphBuilder();
        string count = graph.Variable("count", "int", "0");
        string loop = graph.Node("flow.while");
        string plus = graph.Node("math.int.add");
        string less = graph.Node("compare.int.less");
        string increment = graph.VariableNode(count, set: true);
        string add = graph.Node("math.int.add");
        graph.Then(graph.Node(BuiltInNodes.EntryTypeId), loop).Then(loop, increment, "body")
            .Connect(graph.VariableNode(count, set: false), "value", plus, "a")
            .Connect(graph.Node("literal.int", "1"), "value", plus, "b")
            .Connect(plus, "result", less, "a")
            .Connect(plus, "result", less, "b")
            .Connect(less, "result", loop, "condition")
            .Connect(graph.VariableNode(count, set: false), "value", add, "a")
            .Connect(graph.Node("literal.int", "1"), "value", add, "b")
            .Connect(add, "result", increment, "value");

        Assert.Contains("while (count + 1 < count + 1)", MainBody(graph), StringComparison.Ordinal);
    }
}
