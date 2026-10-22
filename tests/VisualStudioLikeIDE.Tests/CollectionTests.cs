using VisualStudioLikeIDE.Compiler;
using VisualStudioLikeIDE.Compiler.Diagnostics;
using VisualStudioLikeIDE.Compiler.Nodes;
using VisualStudioLikeIDE.Compiler.TypeSystem;

namespace VisualStudioLikeIDE.Tests;

public sealed class CollectionTests
{
    private static string MainBody(GraphBuilder graph) => VariableTests.MainBody(graph);

    [Fact]
    public void ListVariableIsFilledAndSummedWithForEach()
    {
        var graph = new GraphBuilder();
        string numbers = graph.Variable("numbers", "List<int>", "");
        string sum = graph.Variable("sum", "int", "0");
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string addThree = graph.Node("list.int.add");
        string addFour = graph.Node("list.int.add");
        string loop = graph.Node("flow.foreach.int");
        string accumulate = graph.VariableNode(sum, set: true);
        string plus = graph.Node("math.int.add");
        string print = graph.Node("console.write-line");
        string toText = graph.Node("convert.int-to-string");
        graph.Then(entry, addThree).Then(addThree, addFour).Then(addFour, loop).Then(loop, accumulate, "body").Then(loop, print, "completed")
            .Connect(graph.VariableNode(numbers, set: false), "value", addThree, "list")
            .Connect(graph.Node("literal.int", "3"), "value", addThree, "item")
            .Connect(graph.VariableNode(numbers, set: false), "value", addFour, "list")
            .Connect(graph.Node("literal.int", "4"), "value", addFour, "item")
            .Connect(graph.VariableNode(numbers, set: false), "value", loop, "list")
            .Connect(graph.VariableNode(sum, set: false), "value", plus, "a")
            .Connect(loop, "item", plus, "b")
            .Connect(plus, "result", accumulate, "value")
            .Connect(graph.VariableNode(sum, set: false), "value", toText, "value")
            .Connect(toText, "text", print, "text");

        Assert.Equal("""
            var numbers = new List<int>();
            int sum = 0;
            numbers.Add(3);
            numbers.Add(4);
            foreach (int item in numbers)
            {
            sum = sum + item;
            }

            Console.WriteLine(sum.ToString());
            """, MainBody(graph));
    }

    [Fact]
    public void CreatedListsAreReadByIndexAndCountedAndTextIsSplitAndJoined()
    {
        var graph = new GraphBuilder();
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string create = graph.Node("list.string.create");
        string add = graph.Node("list.string.add");
        string set = graph.Node("list.string.set");
        string first = graph.Node("list.string.get");
        string printFirst = graph.Node("console.write-line");
        string split = graph.Node("string.split");
        string join = graph.Node("string.join");
        string printJoined = graph.Node("console.write-line");
        string count = graph.Node("list.string.count");
        string printCount = graph.Node("console.write-line");
        string countText = graph.Node("convert.int-to-string");
        graph.Then(entry, create).Then(create, add).Then(add, set).Then(set, printFirst).Then(printFirst, printJoined).Then(printJoined, printCount)
            .Connect(create, "list", add, "list")
            .Connect(graph.Node("literal.string", "a"), "value", add, "item")
            .Connect(create, "list", set, "list")
            .Connect(graph.Node("literal.int", "0"), "value", set, "index")
            .Connect(graph.Node("literal.string", "b"), "value", set, "item")
            .Connect(create, "list", first, "list")
            .Connect(graph.Node("literal.int", "0"), "value", first, "index")
            .Connect(first, "item", printFirst, "text")
            .Connect(graph.Node("literal.string", "x,y"), "value", split, "text")
            .Connect(graph.Node("literal.string", ","), "value", split, "separator")
            .Connect(graph.Node("literal.string", " "), "value", join, "separator")
            .Connect(split, "parts", join, "list")
            .Connect(join, "text", printJoined, "text")
            .Connect(create, "list", count, "list")
            .Connect(count, "count", countText, "value")
            .Connect(countText, "text", printCount, "text");

        Assert.Equal("""
            var list = new List<string>();
            list.Add("a");
            list[0] = "b";
            Console.WriteLine(list[0]);
            Console.WriteLine(string.Join(" ", new List<string>("x,y".Split(","))));
            Console.WriteLine(list.Count.ToString());
            """, MainBody(graph));
    }

    [Fact]
    public void FunctionsTakeAndReturnLists()
    {
        var function = GraphBuilder.Function("Evens", "List<int>");
        string numbers = function.Parameter("numbers", "List<int>");
        string entry = function.Node(FunctionNodes.EntryTypeId);
        string result = function.Node(FunctionNodes.ReturnTypeId);
        function.Then(entry, result).Connect(entry, numbers, result, "value");

        GraphCompilationResult compiled = CodeGenerationTests.Compile(function);

        Assert.True(compiled.Success, string.Join("\n", compiled.Diagnostics));
        Assert.Contains("using System.Collections.Generic;", compiled.Source!.Text, StringComparison.Ordinal);
        Assert.Contains("public static List<int> Evens(List<int> numbers)", compiled.Source.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ListVariablesMustStartEmpty()
    {
        var graph = new GraphBuilder();
        graph.Variable("numbers", "List<int>", "1, 2");
        graph.Node(BuiltInNodes.EntryTypeId);

        GraphDiagnostic error = Assert.Single(CodeGenerationTests.Compile(graph).Diagnostics, diagnostic => diagnostic.IsError);
        Assert.Equal(DiagnosticIds.InvalidVariable, error.Id);
        Assert.Contains("starts empty", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ListTypesAreNamedLikeCSharp()
    {
        Assert.Equal("List<double>", TypeRef.ListOf(TypeRef.Double).DisplayName);
        Assert.Equal(TypeRef.ListOf(TypeRef.Double), TypeRef.FromName("List<double>"));
        Assert.Equal(TypeRef.ListOf(TypeRef.ListOf(TypeRef.Int32)), TypeRef.FromName("List<List<int>>"));
    }
}
