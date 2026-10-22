using ScriptArt.Compiler;
using ScriptArt.Compiler.Diagnostics;
using ScriptArt.Compiler.Nodes;
using ScriptArt.GraphModel;
using ScriptArt.NodeEditor;
using ScriptArt.ProjectSystem;

namespace ScriptArt.Tests;

public sealed class MethodTests
{
    private static readonly NodeLibrary Library = NodeLibrary.CreateDefault();

    private static GraphBuilder Method(GraphBuilder owner, string name, string? returnType = null)
    {
        GraphBuilder method = GraphBuilder.Function($"{owner.Graph.Name}-{name}", returnType);
        method.Graph.Name = name;
        method.Graph.Owner = owner.Graph.Id;
        return method;
    }

    /// <summary>The generated text from the type declaration on, without the header and namespace.</summary>
    private static string TypeText(string source) => source[source.IndexOf("internal ", StringComparison.Ordinal)..].TrimEnd().ReplaceLineEndings("\n");

    [Fact]
    public void ClassMethodChangesItsObjectThroughSelf()
    {
        GraphBuilder account = GraphBuilder.Type("Account");
        string balance = account.Property("Balance", "int", "0");
        GraphBuilder deposit = Method(account, "Deposit");
        string amount = deposit.Parameter("amount", "int");
        string start = deposit.Node(FunctionNodes.EntryTypeId);
        string set = deposit.TypeNode(TypeNodes.SetTypeId, account.Graph, balance);
        string get = deposit.TypeNode(TypeNodes.GetTypeId, account.Graph, balance);
        string add = deposit.Node("math.int.add");
        deposit.Then(start, set)
            .Connect(deposit.Node(TypeNodes.SelfTypeId), "self", set, "object")
            .Connect(deposit.Node(TypeNodes.SelfTypeId), "self", get, "object")
            .Connect(get, "value", add, "a")
            .Connect(start, amount, add, "b")
            .Connect(add, "result", set, "value");
        var program = new GraphBuilder();
        string entry = program.Node(BuiltInNodes.EntryTypeId);
        string create = program.TypeNode(TypeNodes.CreateTypeId, account.Graph);
        string call = program.Call(deposit.Graph);
        program.Then(entry, create).Then(create, call)
            .Connect(create, "value", call, FunctionNodes.TargetPin)
            .Connect(program.Node("literal.int", "5"), "value", call, amount);

        string[] sources = TypeTests.CompileProject(program, account, deposit);

        Assert.Contains("account.Deposit(5);", sources[0], StringComparison.Ordinal);
        Assert.StartsWith("internal sealed partial class Account", TypeText(sources[1]), StringComparison.Ordinal);
        Assert.Equal("""
            internal sealed partial class Account
            {
                public void Deposit(int amount)
                {
                    this.Balance = this.Balance + amount;
                }
            }
            """, TypeText(sources[2]));
    }

    [Fact]
    public void StructMethodReturnsAValueAndCallsAFunctionOfTheProgramByItsClass()
    {
        GraphBuilder point = GraphBuilder.Type("Point", GraphKind.Struct);
        string x = point.Property("X", "int", "0");
        var square = GraphBuilder.Function("Square", "int");
        string value = square.Parameter("value", "int");
        string squareEntry = square.Node(FunctionNodes.EntryTypeId);
        string squareReturn = square.Node(FunctionNodes.ReturnTypeId);
        string times = square.Node("math.int.multiply");
        square.Then(squareEntry, squareReturn)
            .Connect(squareEntry, value, times, "a")
            .Connect(squareEntry, value, times, "b")
            .Connect(times, "result", squareReturn, "value");
        GraphBuilder size = Method(point, "Size", "int");
        string start = size.Node(FunctionNodes.EntryTypeId);
        string call = size.Call(square.Graph);
        string result = size.Node(FunctionNodes.ReturnTypeId);
        string getX = size.TypeNode(TypeNodes.GetTypeId, point.Graph, x);
        size.Then(start, call).Then(call, result)
            .Connect(size.Node(TypeNodes.SelfTypeId), "self", getX, "object")
            .Connect(getX, "value", call, value)
            .Connect(call, "result", result, "value");
        var program = new GraphBuilder();
        string entry = program.Node(BuiltInNodes.EntryTypeId);
        string callSize = program.Call(size.Graph);
        string print = program.Node("console.write-line");
        string text = program.Node("convert.int-to-string");
        program.Then(entry, callSize).Then(callSize, print)
            .Connect(program.TypeNode(TypeNodes.CreateTypeId, point.Graph), "value", callSize, FunctionNodes.TargetPin)
            .Connect(callSize, "result", text, "value")
            .Connect(text, "text", print, "text");

        string[] sources = TypeTests.CompileProject(program, point, square, size);

        Assert.Contains("int size = new Point().Size();", sources[0], StringComparison.Ordinal);
        Assert.Contains("public static int Square(int value)", sources[2], StringComparison.Ordinal);
        Assert.Equal("""
            internal partial struct Point
            {
                public int Size()
                {
                    int square = Program.Square(this.X);
                    return square;
                }
            }
            """, TypeText(sources[3]));
    }

    [Fact]
    public void MethodsOfDifferentTypesMayShareANameAndGetTheirOwnFiles()
    {
        GraphBuilder a = GraphBuilder.Type("A");
        GraphBuilder b = GraphBuilder.Type("B");
        GraphBuilder resetA = Method(a, "Reset");
        GraphBuilder resetB = Method(b, "Reset");
        resetA.Node(FunctionNodes.EntryTypeId);
        resetB.Node(FunctionNodes.EntryTypeId);
        var program = new GraphBuilder();
        program.Then(program.Node(BuiltInNodes.EntryTypeId), program.PrintLiteral("Hi"));
        GraphDocument[] graphs = [program.Graph, a.Graph, b.Graph, resetA.Graph, resetB.Graph];

        TypeTests.CompileProject(program, a, b, resetA, resetB);

        Assert.Equal("A.Reset.g.cs", GraphCompiler.GeneratedFileName(resetA.Graph, graphs));
        Assert.Equal("B.Reset.g.cs", GraphCompiler.GeneratedFileName(resetB.Graph, graphs));
    }

    [Theory]
    [InlineData("Balance", "Account already has a member named 'Balance'.")]
    [InlineData("Account", "Account already has a member named 'Account'.")]
    public void MethodCannotShareANameWithAMemberOfItsType(string name, string message)
    {
        GraphBuilder account = GraphBuilder.Type("Account");
        account.Property("Balance", "int", "0");
        GraphBuilder method = Method(account, name);
        method.Node(FunctionNodes.EntryTypeId);

        GraphCompilationResult result = TypeTests.Compile(method, account, method);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.InvalidFunction && diagnostic.Message == message);
    }

    [Fact]
    public void MethodOfAMissingTypeIsReported()
    {
        GraphBuilder account = GraphBuilder.Type("Account");
        GraphBuilder method = Method(account, "Close");
        method.Node(FunctionNodes.EntryTypeId);

        GraphCompilationResult result = TypeTests.Compile(method, method);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message == "Method 'Close' belongs to a class or struct that is no longer in the project.");
    }

    [Fact]
    public void SelfIsOfferedOnlyInMethodsAndMethodCallsTakeATarget()
    {
        GraphBuilder account = GraphBuilder.Type("Account");
        GraphBuilder close = Method(account, "Close");
        var program = new GraphBuilder();
        GraphDocument[] graphs = [program.Graph, account.Graph, close.Graph];

        var inMethod = new GraphEditor(close.Graph, Library) { ProjectGraphs = graphs };
        var inProgram = new GraphEditor(program.Graph, Library) { ProjectGraphs = graphs };

        Assert.Contains(inMethod.AvailableDefinitions, definition => definition is SelfNode);
        Assert.DoesNotContain(inProgram.AvailableDefinitions, definition => definition is SelfNode);
        NodeDefinition call = Assert.Single(inProgram.AvailableDefinitions, definition => definition.DisplayName == "Call Account.Close");
        Assert.Equal(new Compiler.TypeSystem.TypeRef("", "Account"), call.FindPin(FunctionNodes.TargetPin)!.DataType);
    }

    [Fact]
    public void OwnerIsSavedAndRead()
    {
        GraphBuilder account = GraphBuilder.Type("Account");
        GraphBuilder close = Method(account, "Close");

        GraphDocument read = GraphSerializer.Deserialize(GraphSerializer.Serialize(close.Graph));

        Assert.Equal(account.Graph.Id, read.Owner);
        Assert.Null(GraphSerializer.Deserialize(GraphSerializer.Serialize(account.Graph)).Owner);
    }

    [Fact]
    public void AddedMethodIsNamedAfterItsTypeOnDisk()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "Demo");
        ProjectGraph account = project.AddType("Account", GraphKind.Class);

        ProjectGraph close = project.AddMethod(account.Graph, "Close");

        Assert.Equal("Graphs/Account.Close.graph.json", close.RelativePath);
        Assert.Equal(account.Graph.Id, close.Graph.Owner);
        Assert.Equal(FunctionNodes.EntryTypeId, Assert.Single(close.Graph.Nodes).Type);
    }
}
