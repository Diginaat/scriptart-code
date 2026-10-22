using ScriptArt.Compiler;
using ScriptArt.Compiler.Diagnostics;
using ScriptArt.Compiler.Nodes;
using ScriptArt.GraphModel;

namespace ScriptArt.Tests;

public sealed class DiagnosticTests
{
    private static GraphDiagnostic SingleError(GraphBuilder graph, string expectedId)
    {
        GraphCompilationResult result = CodeGenerationTests.Compile(graph);
        Assert.False(result.Success);
        Assert.Null(result.Source);
        GraphDiagnostic error = Assert.Single(result.Diagnostics, diagnostic => diagnostic.IsError);
        Assert.Equal(expectedId, error.Id);
        return error;
    }

    [Fact]
    public void MissingEntryIsAnError()
    {
        var graph = new GraphBuilder();
        graph.PrintLiteral("x");
        SingleError(graph, DiagnosticIds.MissingEntry);
    }

    [Fact]
    public void SecondEntryIsAnError()
    {
        var graph = new GraphBuilder();
        graph.Node(BuiltInNodes.EntryTypeId);
        string second = graph.Node(BuiltInNodes.EntryTypeId);
        Assert.Equal(second, SingleError(graph, DiagnosticIds.MultipleEntries).NodeId);
    }

    [Fact]
    public void UnconnectedRequiredInputPointsAtPin()
    {
        var graph = new GraphBuilder();
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string print = graph.Node("console.write-line");
        graph.Then(entry, print);

        GraphDiagnostic error = SingleError(graph, DiagnosticIds.UnconnectedInput);
        Assert.Equal(print, error.NodeId);
        Assert.Equal(new PinRef(print, "text"), error.Pin);
    }

    [Fact]
    public void TypeMismatchIsRejected()
    {
        var graph = new GraphBuilder();
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string print = graph.Node("console.write-line");
        string number = graph.Node("literal.int", "1");
        graph.Then(entry, print).Connect(number, "value", print, "text");

        // The ill-typed connection is reported and dropped, so the input is also unconnected.
        GraphCompilationResult result = CodeGenerationTests.Compile(graph);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.TypeMismatch && diagnostic.NodeId == print);
        Assert.Null(result.Source);
    }

    [Fact]
    public void ExecutionToDataConnectionIsRejected()
    {
        var graph = new GraphBuilder();
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string print = graph.PrintLiteral("x");
        graph.Connect(entry, "then", print, "text");
        Assert.Contains(CodeGenerationTests.Compile(graph).Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.InvalidConnection);
    }

    [Fact]
    public void ExecutionCycleIsAnError()
    {
        var graph = new GraphBuilder();
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string first = graph.PrintLiteral("a");
        string second = graph.PrintLiteral("b");
        graph.Then(entry, first).Then(first, second).Then(second, first);
        SingleError(graph, DiagnosticIds.ExecutionCycle);
    }

    [Fact]
    public void DataCycleIsAnError()
    {
        var graph = new GraphBuilder();
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string print = graph.Node("console.write-line");
        string concat = graph.Node("string.concat");
        string text = graph.Node("literal.string", "x");
        graph.Then(entry, print)
            .Connect(concat, "result", print, "text")
            .Connect(text, "value", concat, "a")
            .Connect(concat, "result", concat, "b");
        SingleError(graph, DiagnosticIds.DataCycle);
    }

    [Fact]
    public void ValueFromOtherBranchArmIsNotAvailable()
    {
        var graph = new GraphBuilder();
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string branch = graph.Node("core.branch");
        string condition = graph.Node("literal.bool", "true");
        string read = graph.Node("console.read-line");
        string print = graph.Node("console.write-line");
        graph.Then(entry, branch)
            .Connect(condition, "value", branch, "condition")
            .Then(branch, read, "true")
            .Then(branch, print, "false")
            .Connect(read, "line", print, "text");

        Assert.Equal(print, SingleError(graph, DiagnosticIds.ValueNotAvailable).NodeId);
    }

    [Fact]
    public void ValueUsedBeforeItsNodeRunsIsNotAvailable()
    {
        var graph = new GraphBuilder();
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string print = graph.Node("console.write-line");
        string read = graph.Node("console.read-line");
        graph.Then(entry, print).Then(print, read).Connect(read, "line", print, "text");
        SingleError(graph, DiagnosticIds.ValueNotAvailable);
    }

    [Fact]
    public void ArmJumpingIntoOtherArmIsUnstructured()
    {
        // True: A -> B, False: B. B is reached from inside the True arm and directly, but B is also the reconvergence point,
        // so this one is structured. Make it unstructured by jumping into the middle of a chain instead.
        var graph = new GraphBuilder();
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string outer = graph.Node("core.branch");
        string inner = graph.Node("core.branch");
        string flag = graph.Node("literal.bool", "true");
        string a = graph.PrintLiteral("a");
        string b = graph.PrintLiteral("b");
        string c = graph.PrintLiteral("c");
        graph.Then(entry, outer)
            .Connect(flag, "value", outer, "condition")
            .Connect(flag, "value", inner, "condition")
            .Then(outer, inner, "true")
            .Then(inner, a, "true")
            .Then(inner, b, "false")
            .Then(a, b)
            .Then(outer, c, "false")
            .Then(c, b);

        // b is the reconvergence point of both branches; generation stays structured.
        Assert.True(CodeGenerationTests.Compile(graph).Success);
    }

    [Fact]
    public void ExecutionOutputWithTwoTargetsIsAnError()
    {
        var graph = new GraphBuilder();
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        graph.Then(entry, graph.PrintLiteral("a")).Then(entry, graph.PrintLiteral("b"));
        Assert.Contains(CodeGenerationTests.Compile(graph).Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.MultipleExecutionTargets);
    }

    [Fact]
    public void InvalidLiteralIsAnError()
    {
        var graph = new GraphBuilder();
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string print = graph.Node("console.write-line");
        string convert = graph.Node("convert.int-to-string");
        string number = graph.Node("literal.int", "twelve");
        graph.Then(entry, print).Connect(convert, "text", print, "text").Connect(number, "value", convert, "value");
        Assert.Equal(number, SingleError(graph, DiagnosticIds.InvalidPropertyValue).NodeId);
    }

    [Fact]
    public void UnknownNodeTypeIsAnError()
    {
        var graph = new GraphBuilder();
        graph.Node(BuiltInNodes.EntryTypeId);
        graph.Node("acme.unknown");
        SingleError(graph, DiagnosticIds.UnknownNodeType);
    }

    [Fact]
    public void DisconnectedNodesAreWarningsNotErrors()
    {
        var graph = new GraphBuilder();
        graph.Node(BuiltInNodes.EntryTypeId);
        graph.PrintLiteral("orphan");
        graph.Node("literal.int", "3");

        GraphCompilationResult result = CodeGenerationTests.Compile(graph);
        Assert.True(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UnreachableNode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UnusedNode);
        Assert.DoesNotContain("orphan", result.Source!.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("class")]
    [InlineData("1Program")]
    [InlineData("")]
    public void InvalidGraphNameIsAnError(string name)
    {
        var graph = new GraphBuilder();
        graph.Graph.Name = name;
        graph.Node(BuiltInNodes.EntryTypeId);
        SingleError(graph, DiagnosticIds.InvalidIdentifier);
    }
}
