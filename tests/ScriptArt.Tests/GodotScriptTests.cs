using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ScriptArt.Compiler;
using ScriptArt.Compiler.Diagnostics;
using ScriptArt.Compiler.Emit;
using ScriptArt.Compiler.Nodes;
using ScriptArt.Compiler.TypeSystem;
using ScriptArt.GraphModel;
using ScriptArt.NodeEditor;
using ScriptArt.ProjectSystem;

namespace ScriptArt.Tests;

/// <summary>
/// Classes that derive from a .NET class, as Godot scripts do (Phase 7, D-047). A small stand-in for GodotSharp, compiled at test
/// time, plays the part of Godot's API.
/// </summary>
public sealed class GodotScriptTests : IDisposable
{
    private const string GodotApi = """
        namespace Godot;

        public class GodotObject
        {
            public Error EmitSignal(StringName signal, params Variant[] args) => Error.Ok;
            public Error Connect(StringName signal, Callable callable, uint flags = 0) => Error.Ok;
        }

        public enum Error { Ok }

        public struct Variant
        {
            public static implicit operator Variant(bool from) => default;
            public static implicit operator Variant(int from) => default;
            public static implicit operator Variant(double from) => default;
            public static implicit operator Variant(string from) => default;
        }

        public readonly struct Callable
        {
            public static Callable From(System.Action action) => default;
            public static Callable From<T0>(System.Action<T0> action) => default;
        }

        [System.AttributeUsage(System.AttributeTargets.Delegate)]
        public sealed class SignalAttribute : System.Attribute { }

        public class BaseButton : Node
        {
            public event System.Action? Pressed;
        }

        public class Button : BaseButton { }

        public class Area2D : Node2D
        {
            public delegate void BodyEnteredEventHandler(Node2D body);

            public event BodyEnteredEventHandler? BodyEntered;
        }
        [System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Field)]
        public sealed class ExportAttribute : System.Attribute { }

        public class Node : GodotObject
        {
            public string Name { get; set; } = "";
            public virtual void _Ready() { }
            public virtual void _Process(double delta) { }
            public virtual void _PhysicsProcess(double delta) { }
            public virtual void _Input(InputEvent @event) { }
            public void QueueFree() { }
            public T GetNode<T>(NodePath path) where T : class => null!;
        }

        public class InputEvent : GodotObject { }

        public class InputEventKey : InputEvent
        {
            public bool Echo { get; set; }
        }

        public class Label : Node
        {
            public string Text { get; set; } = "";
        }

        public sealed class NodePath
        {
            public NodePath(string path) { }
            public static implicit operator NodePath(string from) => new(from);
            public static implicit operator string(NodePath from) => "";
        }

        public class Node2D : Node
        {
            public float Rotation { get; set; }
        }

        public class CharacterBody2D : Node2D
        {
            public Vector2 Velocity { get; set; }
            public bool MoveAndSlide() => true;
        }

        public struct Vector2
        {
            public float X;
            public float Y;
            public Vector2(float x, float y) { X = x; Y = y; }
            public static Vector2 operator +(Vector2 left, Vector2 right) => new(left.X + right.X, left.Y + right.Y);
            public static Vector2 operator *(Vector2 vec, float scale) => new(vec.X * scale, vec.Y * scale);
            public static Vector2 operator -(Vector2 vec) => new(-vec.X, -vec.Y);
            public static bool operator ==(Vector2 left, Vector2 right) => left.X == right.X && left.Y == right.Y;
            public static bool operator !=(Vector2 left, Vector2 right) => !(left == right);
            public override bool Equals(object? obj) => obj is Vector2 other && this == other;
            public override int GetHashCode() => 0;
        }

        public sealed class StringName
        {
            public StringName(string name) { }
            public static implicit operator StringName(string from) => new(from);
            public static implicit operator string(StringName from) => "";
        }

        public static class Input
        {
            public static float GetAxis(StringName negativeAction, StringName positiveAction) => 0;
        }

        public sealed class Locked : Node { }

        public static class GD
        {
            public static void Print(string what) { }
        }
        """;

    private readonly TemporaryDirectory directory = new();
    private readonly string godotAssembly;
    private readonly NodeLibrary library;

    public GodotScriptTests()
    {
        godotAssembly = Path.Combine(directory.Path, "GodotSharp.dll");
        CSharpCompilation compilation = CSharpCompilation.Create("GodotSharp",
            [CSharpSyntaxTree.ParseText(GodotApi, cancellationToken: TestContext.Current.CancellationToken)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.True(compilation.Emit(godotAssembly, cancellationToken: TestContext.Current.CancellationToken).Success);
        library = NodeLibrary.CreateDefault([godotAssembly]);
    }

    public void Dispose() => directory.Dispose();

    private static GraphBuilder Script(string name, string baseType)
    {
        GraphBuilder script = GraphBuilder.Type(name);
        script.Graph.BaseType = baseType;
        return script;
    }

    private static GraphBuilder Method(GraphBuilder owner, string name, string? returnType = null)
    {
        GraphBuilder method = GraphBuilder.Function($"{owner.Graph.Name}-{name}", returnType);
        method.Graph.Name = name;
        method.Graph.Owner = owner.Graph.Id;
        return method;
    }

    private GraphCompilationResult Compile(GraphBuilder graph, GraphBuilder[] project) =>
        new GraphCompiler(library).Compile(graph.Graph, new GraphCompilationOptions("Game", [.. project.Select(other => other.Graph)]), "Graphs/Test.graph.json");

    // Compiles every graph, checks that Roslyn accepts them together with the Godot stand-in, and returns each generated file.
    private GeneratedSource[] CompileProject(params GraphBuilder[] project)
    {
        GeneratedSource[] sources = [.. project.Select(graph =>
        {
            GraphCompilationResult result = Compile(graph, project);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            return result.Source!;
        })];
        IReadOnlyList<GraphDiagnostic> roslyn = RoslynVerifier.Verify("Game",
            [.. sources.Select(source => new VerificationSource(source.RelativePath, source.Text, source))], isExecutable: false, [godotAssembly]);
        Assert.Empty(roslyn);
        return sources;
    }

    private static string TypeText(GeneratedSource source) => source.Text[source.Text.IndexOf("public partial", StringComparison.Ordinal)..].TrimEnd().ReplaceLineEndings("\n");

    [Fact]
    public void AScriptClassDerivesFromItsBaseAndOverridesItsVirtualMethods()
    {
        GraphBuilder player = Script("Player", "Godot.CharacterBody2D");
        player.Property("Speed", "double", "200");
        GraphBuilder ready = Method(player, "_Ready");
        string readyEntry = ready.Node(FunctionNodes.EntryTypeId);
        string print = ready.Node("dotnet:M:Godot.GD.Print(System.String)");
        ready.Then(readyEntry, print).Connect(ready.Node("literal.string", "ready"), "value", print, "what");

        // Self is a CharacterBody2D, so it connects to an input of Node2D, a class it derives from.
        GraphBuilder process = Method(player, "_Process");
        string delta = process.Parameter("delta", "double");
        string processEntry = process.Node(FunctionNodes.EntryTypeId);
        string rotate = process.Node("dotnet:set:P:Godot.Node2D.Rotation");
        process.Then(processEntry, rotate)
            .Connect(process.Node(TypeNodes.SelfTypeId), "self", rotate, "instance")
            .Connect(processEntry, delta, rotate, "value");

        GeneratedSource[] sources = CompileProject(player, ready, process);

        Assert.Equal(["Player.cs", "Player._Ready.g.cs", "Player._Process.g.cs"], sources.Select(source => source.RelativePath));
        Assert.Contains("using Godot;", sources[0].Text, StringComparison.Ordinal);
        Assert.Equal("""
            public partial class Player : CharacterBody2D
            {
                internal double Speed { get; set; } = 200.0;
            }
            """, TypeText(sources[0]));
        Assert.Equal("""
            public partial class Player : CharacterBody2D
            {
                public override void _Ready()
                {
                    GD.Print("ready");
                }
            }
            """, TypeText(sources[1]));
        Assert.Equal("""
            public partial class Player : CharacterBody2D
            {
                public override void _Process(double delta)
                {
                    this.Rotation = (float)delta;
                }
            }
            """, TypeText(sources[2]));
    }

    [Fact]
    public void FloatsAreDoublesAndStringNamesAreStringsToGraphs()
    {
        // The moving character of the Godot sample: Velocity = new Vector2(Input.GetAxis("ui_left", "ui_right") * Speed, Velocity.Y).
        GraphBuilder player = Script("Player", "Godot.CharacterBody2D");
        player.Property("Speed", "double", "300");
        GraphBuilder physics = Method(player, "_PhysicsProcess");
        physics.Parameter("delta", "double");
        string entry = physics.Node(FunctionNodes.EntryTypeId);
        string axis = physics.Node("dotnet:M:Godot.Input.GetAxis(Godot.StringName,Godot.StringName)");
        string speed = physics.TypeNode(TypeNodes.GetTypeId, player.Graph, player.Graph.Properties[0].Id);
        string times = physics.Node("math.double.multiply");
        string velocity = physics.Node("dotnet:P:Godot.CharacterBody2D.Velocity");
        string y = physics.Node("dotnet:F:Godot.Vector2.Y");
        string vector = physics.Node("dotnet:M:Godot.Vector2.#ctor(System.Single,System.Single)");
        string set = physics.Node("dotnet:set:P:Godot.CharacterBody2D.Velocity");
        string move = physics.Node("dotnet:M:Godot.CharacterBody2D.MoveAndSlide");
        physics.Then(entry, axis).Then(axis, set).Then(set, move)
            .Connect(physics.Node(TypeNodes.SelfTypeId), "self", move, "instance")
            .Connect(physics.Node("literal.string", "ui_left"), "value", axis, "negativeAction")
            .Connect(physics.Node("literal.string", "ui_right"), "value", axis, "positiveAction")
            .Connect(physics.Node(TypeNodes.SelfTypeId), "self", speed, "object")
            .Connect(axis, "result", times, "a")
            .Connect(speed, "value", times, "b")
            .Connect(physics.Node(TypeNodes.SelfTypeId), "self", velocity, "instance")
            .Connect(velocity, "result", y, "instance")
            .Connect(times, "result", vector, "x")
            .Connect(y, "result", vector, "y")
            .Connect(physics.Node(TypeNodes.SelfTypeId), "self", set, "instance")
            .Connect(vector, "result", set, "value");

        GeneratedSource[] sources = CompileProject(player, physics);

        Assert.Contains("double getAxis = Input.GetAxis(\"ui_left\", \"ui_right\");", sources[1].Text, StringComparison.Ordinal);
        Assert.Contains("this.Velocity = new Vector2((float)(getAxis * this.Speed), (float)this.Velocity.Y);", sources[1].Text, StringComparison.Ordinal);
        Assert.Contains("this.MoveAndSlide();", sources[1].Text, StringComparison.Ordinal);
        Assert.Contains("public override void _PhysicsProcess(double delta)", sources[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void OperatorsOfATypeAreNodesWrittenAsOperators()
    {
        Assert.Equal("Vector2 * double", library.Find("dotnet:M:Godot.Vector2.op_Multiply(Godot.Vector2,System.Single)")!.DisplayName);
        Assert.Equal("-Vector2", library.Find("dotnet:M:Godot.Vector2.op_UnaryNegation(Godot.Vector2)")!.DisplayName);

        // Velocity = -(Velocity + Velocity * delta)
        GraphBuilder player = Script("Player", "Godot.CharacterBody2D");
        GraphBuilder process = Method(player, "_Process");
        string delta = process.Parameter("delta", "double");
        string entry = process.Node(FunctionNodes.EntryTypeId);
        string velocity = process.Node("dotnet:P:Godot.CharacterBody2D.Velocity");
        string scale = process.Node("dotnet:M:Godot.Vector2.op_Multiply(Godot.Vector2,System.Single)");
        string add = process.Node("dotnet:M:Godot.Vector2.op_Addition(Godot.Vector2,Godot.Vector2)");
        string negate = process.Node("dotnet:M:Godot.Vector2.op_UnaryNegation(Godot.Vector2)");
        string set = process.Node("dotnet:set:P:Godot.CharacterBody2D.Velocity");
        process.Then(entry, set)
            .Connect(process.Node(TypeNodes.SelfTypeId), "self", velocity, "instance")
            .Connect(velocity, "result", scale, "vec")
            .Connect(entry, delta, scale, "scale")
            .Connect(velocity, "result", add, "left")
            .Connect(scale, "result", add, "right")
            .Connect(add, "result", negate, "vec")
            .Connect(process.Node(TypeNodes.SelfTypeId), "self", set, "instance")
            .Connect(negate, "result", set, "value");

        GeneratedSource[] sources = CompileProject(player, process);

        // Velocity feeds two inputs, so it is read once.
        Assert.Contains("var result = this.Velocity;\n        this.Velocity = -(result + result * (float)delta);", sources[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void OtherMethodsOfAScriptClassAreInternalAndOverrideNothing()
    {
        GraphBuilder player = Script("Player", "Godot.Node2D");
        GraphBuilder jump = Method(player, "Jump");
        jump.Node(FunctionNodes.EntryTypeId);

        // _Process with other parameter types is an overload, not an override.
        GraphBuilder process = Method(player, "_Process");
        process.Parameter("frames", "int");
        process.Node(FunctionNodes.EntryTypeId);

        GeneratedSource[] sources = CompileProject(player, jump, process);

        Assert.Contains("    internal void Jump()", sources[1].Text, StringComparison.Ordinal);
        Assert.Contains("    internal void _Process(int frames)", sources[2].Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Godot.Locked", "sealed")]
    [InlineData("Godot.Missing", "not offered by the project's references")]
    [InlineData("Godot.GD", "not offered by the project's references")]
    [InlineData("Player", "not the full name of a .NET class")]
    public void ABaseTypeMustBeAClassThatCanBeDerivedFrom(string baseType, string expected)
    {
        GraphBuilder player = Script("Player", baseType);

        GraphCompilationResult result = Compile(player, [player]);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.IsError && diagnostic.Message.Contains(expected, StringComparison.Ordinal));
        Assert.Null(result.Source);
    }

    [Fact]
    public void ExportedPropertiesGetGodotsExportAttribute()
    {
        GraphBuilder player = Script("Player", "Godot.CharacterBody2D");
        string speed = player.Property("Speed", "double", "200");
        player.Property("Lives", "int", "3");
        player.Graph.FindProperty(speed)!.Export = true;

        GeneratedSource[] sources = CompileProject(player);

        Assert.Contains("[Export]\n    internal double Speed { get; set; } = 200.0;\n    internal int Lives { get; set; } = 3;", TypeText(sources[0]), StringComparison.Ordinal);
        Assert.True(GraphSerializer.Deserialize(GraphSerializer.Serialize(player.Graph)).FindProperty(speed)!.Export);
        Assert.True(player.Graph.Clone().FindProperty(speed)!.Export);
    }

    [Theory]
    [InlineData(null, "double", "only a class deriving from a Godot class")]
    [InlineData("Godot.CharacterBody2D", "List<int>", "Godot cannot export")]
    public void OnlyGodotClassesExportAndOnlyTypesGodotKnows(string? baseType, string type, string expected)
    {
        GraphBuilder player = GraphBuilder.Type("Player");
        player.Graph.BaseType = baseType;
        player.Graph.FindProperty(player.Property("Value", type, type == "double" ? "1" : ""))!.Export = true;

        GraphCompilationResult result = Compile(player, [player]);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.IsError && diagnostic.Message.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void ExportingAPropertyCanBeUndone()
    {
        GraphBuilder player = Script("Player", "Godot.Node");
        string speed = player.Property("Speed", "double");
        var editor = new GraphEditor(player.Graph, library);

        editor.SetPropertyExport(speed, true);
        Assert.True(player.Graph.FindProperty(speed)!.Export);
        editor.Undo();
        Assert.False(player.Graph.FindProperty(speed)!.Export);
    }

    private const string GetNodeId = "dotnet:M:Godot.Node.GetNode``1(Godot.NodePath)";

    // _Ready: GetNode<typeArguments>("Score").Text = "Ready", where GetNode is a statement like any method call
    private static (GraphBuilder Player, GraphBuilder Ready, string GetNode) SetsALabel(string typeArguments)
    {
        GraphBuilder player = Script("Player", "Godot.Node");
        GraphBuilder ready = Method(player, "_Ready");
        string entry = ready.Node(FunctionNodes.EntryTypeId);
        string getNode = ready.Node(GetNodeId);
        ready.Graph.FindNode(getNode)!.Properties[ExternalApi.TypeArgumentsProperty] = typeArguments;
        string set = ready.Node("dotnet:set:P:Godot.Label.Text");
        ready.Then(entry, getNode).Then(getNode, set)
            .Connect(ready.Node(TypeNodes.SelfTypeId), "self", getNode, "instance")
            .Connect(ready.Node("literal.string", "Score"), "value", getNode, "path")
            .Connect(getNode, "result", set, "instance")
            .Connect(ready.Node("literal.string", "Ready"), "value", set, "value");
        return (player, ready, getNode);
    }

    [Fact]
    public void GetNodeTakesAClassAsItsTypeArgument()
    {
        (GraphBuilder player, GraphBuilder ready, _) = SetsALabel("Godot.Label");

        GeneratedSource[] sources = CompileProject(player, ready);

        Assert.Contains("var getNode = this.GetNode<Label>(\"Score\");\n        getNode.Text = \"Ready\";", sources[1].Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("int")]
    [InlineData("Godot.Vector2")]
    public void GetNodeRejectsTypeArgumentsThatAreNotClasses(string typeArguments)
    {
        (GraphBuilder player, GraphBuilder ready, string getNode) = SetsALabel(typeArguments);

        GraphCompilationResult result = Compile(ready, [player, ready]);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.IsError && diagnostic.NodeId == getNode
            && diagnostic.Message.Contains("Type Arguments are not types the method can use", StringComparison.Ordinal));
    }

    [Fact]
    public void AStructCannotHaveABaseType()
    {
        GraphBuilder point = GraphBuilder.Type("Point", GraphKind.Struct);
        point.Graph.BaseType = "Godot.Node";

        GraphCompilationResult result = Compile(point, [point]);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.IsError && diagnostic.Message.Contains("struct cannot derive", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOverrideMustReturnWhatTheVirtualMethodReturns()
    {
        GraphBuilder player = Script("Player", "Godot.Node");
        GraphBuilder ready = Method(player, "_Ready", "int");
        string entry = ready.Node(FunctionNodes.EntryTypeId);
        string result = ready.Node(FunctionNodes.ReturnTypeId);
        ready.Then(entry, result).Connect(ready.Node("literal.int", "1"), "value", result, "value");

        GraphCompilationResult compiled = Compile(ready, [player, ready]);

        Assert.Contains(compiled.Diagnostics, diagnostic => diagnostic.IsError && diagnostic.Message.Contains("must return void", StringComparison.Ordinal));
    }

    [Fact]
    public void ValuesConnectToInputsOfTheClassesTheyDeriveFrom()
    {
        GraphBuilder player = Script("Player", "Godot.CharacterBody2D");
        GraphBuilder jump = Method(player, "Jump");
        string self = jump.Node(TypeNodes.SelfTypeId);
        string rename = jump.Node("dotnet:set:P:Godot.Node.Name");
        var editor = new GraphEditor(jump.Graph, library) { ProjectGraphs = [player.Graph, jump.Graph] };

        Assert.True(editor.Connect(new PinRef(self, "self"), new PinRef(rename, "instance")).IsAllowed);
        Assert.Equal([TypeRef.External("Godot", "CharacterBody2D"), TypeRef.External("Godot", "Node2D"), TypeRef.External("Godot", "Node"), TypeRef.External("Godot", "GodotObject")],
            library.BaseTypesOf(TypeRef.ProjectType("Player"), [player.Graph]));

        // A Node is not a Node2D: base types connect upward only.
        string node = jump.Node("dotnet:M:Godot.Node.QueueFree");
        Assert.False(editor.Connect(new PinRef(rename, "instance"), new PinRef(node, "instance")).IsAllowed);
    }

    [Fact]
    public void TheBaseTypeIsSavedAndReadBack()
    {
        GraphBuilder player = Script("Player", "Godot.Node2D");

        GraphDocument read = GraphSerializer.Deserialize(GraphSerializer.Serialize(player.Graph));

        Assert.Equal("Godot.Node2D", read.BaseType);
        Assert.Equal("Godot.Node2D", read.Clone().BaseType);
        Assert.DoesNotContain("baseType", GraphSerializer.Serialize(GraphBuilder.Type("Plain").Graph), StringComparison.Ordinal);
    }

    [Fact]
    public void TheBaseClassIsSetAsOneUndoableStep()
    {
        GraphBuilder player = GraphBuilder.Type("Player");
        var editor = new GraphEditor(player.Graph, library);

        editor.SetBaseType("  Godot.Node2D ");
        Assert.Equal("Godot.Node2D", player.Graph.BaseType);
        editor.SetBaseType("");
        Assert.Null(player.Graph.BaseType);
        editor.Undo();
        Assert.Equal("Godot.Node2D", player.Graph.BaseType);
        editor.Undo();
        Assert.Null(player.Graph.BaseType);
    }

    [Fact]
    public void TheVirtualMethodsOfEveryBaseClassCanBeOverridden()
    {
        GraphBuilder player = Script("Player", "Godot.CharacterBody2D");

        Assert.Equal(["_Input", "_PhysicsProcess", "_Process", "_Ready"], library.OverridableMethods(player.Graph, [player.Graph]).Select(method => method.Name));
        Assert.Empty(library.OverridableMethods(GraphBuilder.Type("Plain").Graph, []));
    }

    [Fact]
    public void AnOverriddenParameterNamedWithAKeywordIsRenamed()
    {
        using var folder = new TemporaryDirectory();
        VisualProject project = VisualProject.CreateGodot(folder.Path, "Game");
        GraphDocument main = project.GraphDocuments.Single(graph => graph.Kind == GraphKind.Class);
        ExternalMethod input = library.OverridableMethods(main, project.GraphDocuments).Single(method => method.Name == "_Input");

        GraphDocument method = project.AddOverride(main, input).Graph;

        Assert.Equal("_Input", method.Name);
        Assert.Equal(main.Id, method.Owner);
        GraphParameter parameter = Assert.Single(method.Parameters);
        Assert.Equal(("theEvent", "Godot.InputEvent"), (parameter.Name, parameter.Type));
    }

    // _Input(theEvent): when it is a key press, sets its Echo (in Succeeded), else prints "other"; then prints "done".
    private static (GraphBuilder Player, GraphBuilder Input, string Cast, string Done) ChecksForAKey(string castType)
    {
        GraphBuilder player = Script("Player", "Godot.Node");
        GraphBuilder input = Method(player, "_Input");
        string theEvent = input.Parameter("theEvent", "Godot.InputEvent");
        string entry = input.Node(FunctionNodes.EntryTypeId);
        string cast = input.Node(TypeNodes.CastTypeId);
        input.Graph.FindNode(cast)!.Properties[TypeNodes.CastProperty] = castType;
        string echo = input.Node("dotnet:set:P:Godot.InputEventKey.Echo");
        string other = input.Node("dotnet:M:Godot.GD.Print(System.String)");
        string done = input.Node("dotnet:M:Godot.GD.Print(System.String)");
        input.Then(entry, cast)
            .Connect(entry, theEvent, cast, "object")
            .Then(cast, echo, "succeeded")
            .Connect(cast, "result", echo, "instance")
            .Connect(input.Node("literal.bool", "true"), "value", echo, "value")
            .Then(cast, other, "failed")
            .Connect(input.Node("literal.string", "other"), "value", other, "what")
            .Then(cast, done, "completed")
            .Connect(input.Node("literal.string", "done"), "value", done, "what");
        return (player, input, cast, done);
    }

    [Fact]
    public void CastToRunsSucceededWithTheObjectAsTheClass()
    {
        (GraphBuilder player, GraphBuilder input, _, _) = ChecksForAKey("Godot.InputEventKey");

        GeneratedSource[] sources = CompileProject(player, input);

        Assert.Contains("""
                public override void _Input(InputEvent theEvent)
                {
                    if (theEvent is InputEventKey inputEventKey)
                    {
                        inputEventKey.Echo = true;
                    }
                    else
                    {
                        GD.Print("other");
                    }

                    GD.Print("done");
                }
            """.ReplaceLineEndings("\n"), sources[1].Text, StringComparison.Ordinal);
        Assert.Equal("Cast To InputEventKey", library.Resolve(input.Graph.Nodes[1], input.Graph, [player.Graph, input.Graph])!.DisplayName);
    }

    [Fact]
    public void CastToOffersEveryClassAndAcceptsAnyClass()
    {
        GraphBuilder player = Script("Player", "Godot.Node");
        GraphBuilder input = Method(player, "_Input");
        string[] casts = [.. library.DefinitionsFor(input.Graph, [player.Graph, input.Graph]).OfType<CastNode>().Select(cast => cast.Type.SourceName)];

        Assert.Contains("Godot.InputEventKey", casts);
        Assert.Contains("Player", casts);
        Assert.DoesNotContain("Godot.Vector2", casts);
        Assert.Contains(TypeRef.Object, library.AssignableTypesOf(TypeRef.FromName("Godot.InputEvent")!, [player.Graph]));
        Assert.Contains(TypeRef.Object, library.AssignableTypesOf(TypeRef.ProjectType("Player"), [player.Graph]));
        Assert.DoesNotContain(TypeRef.Object, library.AssignableTypesOf(TypeRef.Int32, [player.Graph]));
    }

    [Fact]
    public void CastToReportsAClassItCannotCheckFor()
    {
        (GraphBuilder player, GraphBuilder input, string cast, _) = ChecksForAKey("Godot.Vector2");

        GraphCompilationResult result = Compile(input, [player, input]);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.IsError && diagnostic.NodeId == cast
            && diagnostic.Message.Contains("Cast To needs a class", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCastObjectIsOnlyAvailableInSucceeded()
    {
        (GraphBuilder player, GraphBuilder input, string cast, string done) = ChecksForAKey("Godot.InputEventKey");
        string echo = input.Node("dotnet:P:Godot.InputEventKey.Echo");
        input.Connect(cast, "result", echo, "instance");
        input.Graph.Connections.RemoveAll(connection => connection.To == new PinRef(done, "what"));
        string text = input.Node("convert.bool-to-string");
        input.Connect(echo, "result", text, "value").Connect(text, "text", done, "what");

        GraphCompilationResult result = Compile(input, [player, input]);

        // Completed runs after both branches, where the object may not be an InputEventKey.
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.IsError && diagnostic.Id == DiagnosticIds.ValueNotAvailable);
    }

    [Fact]
    public void OnABuiltInSignalRunsItsHandlerEachTimeItIsEmitted()
    {
        // _Ready: GetNode<Button>("Start").Pressed += () => GD.Print("pressed"); then GD.Print("ready").
        GraphBuilder player = Script("Player", "Godot.Node");
        GraphBuilder ready = Method(player, "_Ready");
        string entry = ready.Node(FunctionNodes.EntryTypeId);
        string getNode = ready.Node(GetNodeId);
        ready.Graph.FindNode(getNode)!.Properties[ExternalApi.TypeArgumentsProperty] = "Godot.Button";
        string on = ready.Node("dotnet:E:Godot.BaseButton.Pressed");
        string pressed = ready.Node("dotnet:M:Godot.GD.Print(System.String)");
        string done = ready.Node("dotnet:M:Godot.GD.Print(System.String)");
        ready.Then(entry, getNode).Then(getNode, on).Then(on, pressed, "handler").Then(on, done)
            .Connect(ready.Node(TypeNodes.SelfTypeId), "self", getNode, "instance")
            .Connect(ready.Node("literal.string", "Start"), "value", getNode, "path")
            .Connect(getNode, "result", on, "instance")
            .Connect(ready.Node("literal.string", "pressed"), "value", pressed, "what")
            .Connect(ready.Node("literal.string", "ready"), "value", done, "what");

        GeneratedSource[] sources = CompileProject(player, ready);

        Assert.Equal("On BaseButton.Pressed", library.Find("dotnet:E:Godot.BaseButton.Pressed")!.DisplayName);
        Assert.Contains("getNode.Pressed += () =>\n        {\n            GD.Print(\"pressed\");\n        };\n        GD.Print(\"ready\");",
            sources[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheValuesASignalSendsAreOutputsOfItsHandler()
    {
        // _Ready: BodyEntered += body => GD.Print(body.Name), on the script's own Area2D.
        GraphBuilder area = Script("Pickup", "Godot.Area2D");
        GraphBuilder ready = Method(area, "_Ready");
        string entry = ready.Node(FunctionNodes.EntryTypeId);
        string on = ready.Node("dotnet:E:Godot.Area2D.BodyEntered");
        string name = ready.Node("dotnet:P:Godot.Node.Name");
        string print = ready.Node("dotnet:M:Godot.GD.Print(System.String)");
        ready.Then(entry, on).Then(on, print, "handler")
            .Connect(ready.Node(TypeNodes.SelfTypeId), "self", on, "instance")
            .Connect(on, "body", name, "instance")
            .Connect(name, "result", print, "what");

        GeneratedSource[] sources = CompileProject(area, ready);

        Assert.Contains("this.BodyEntered += (body) =>\n        {\n            GD.Print(body.Name);\n        };", sources[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AClassDeclaresEmitsAndHandlesItsOwnSignals()
    {
        GraphBuilder player = Script("Player", "Godot.Node");
        var editor = new GraphEditor(player.Graph, library);
        GraphSignal hit = editor.AddSignal();
        editor.RenameSignal(hit.Id, "Hit");
        Assert.True(editor.SetSignalParameters(hit.Id, "int damage"));

        // TakeHit: EmitSignal("Hit", 5). _Ready: Connect("Hit", Callable.From((int damage) => GD.Print(damage.ToString()))).
        GraphBuilder takeHit = Method(player, "TakeHit");
        string entry = takeHit.Node(FunctionNodes.EntryTypeId);
        string emit = takeHit.Node(SignalNodes.EmitTypeId);
        SetSignal(takeHit, emit, player, hit);
        string damage = hit.Parameters[0].Id;
        takeHit.Then(entry, emit)
            .Connect(takeHit.Node(TypeNodes.SelfTypeId), "self", emit, "instance")
            .Connect(takeHit.Node("literal.int", "5"), "value", emit, damage);
        GraphBuilder ready = Method(player, "_Ready");
        string readyEntry = ready.Node(FunctionNodes.EntryTypeId);
        string on = ready.Node(SignalNodes.OnTypeId);
        SetSignal(ready, on, player, hit);
        string text = ready.Node("convert.int-to-string");
        string print = ready.Node("dotnet:M:Godot.GD.Print(System.String)");
        ready.Then(readyEntry, on).Then(on, print, "handler")
            .Connect(ready.Node(TypeNodes.SelfTypeId), "self", on, "instance")
            .Connect(on, damage, text, "value")
            .Connect(text, "text", print, "what");

        GeneratedSource[] sources = CompileProject(player, takeHit, ready);

        Assert.Contains("[Signal]\n    public delegate void HitEventHandler(int damage);", sources[0].Text, StringComparison.Ordinal);
        Assert.Contains("this.EmitSignal(\"Hit\", 5);", sources[1].Text, StringComparison.Ordinal);
        Assert.Contains("this.Connect(\"Hit\", Callable.From((int damage) =>\n        {\n            GD.Print(damage.ToString());\n        }));", sources[2].Text, StringComparison.Ordinal);
        Assert.Equal("Emit Player.Hit", library.Resolve(takeHit.Graph.FindNode(emit)!, takeHit.Graph, [player.Graph, takeHit.Graph, ready.Graph])!.DisplayName);
    }

    private static void SetSignal(GraphBuilder graph, string node, GraphBuilder owner, GraphSignal signal)
    {
        graph.Graph.FindNode(node)!.Properties[TypeNodes.TypeProperty] = owner.Graph.Id;
        graph.Graph.FindNode(node)!.Properties[SignalNodes.SignalProperty] = signal.Id;
    }

    [Theory]
    [InlineData(null, "Hit", "int damage", "only a class deriving from a Godot class")]
    [InlineData("Godot.Node", "Hit", "List<int> values", "Godot cannot send")]
    [InlineData("Godot.Node", "1Hit", "", "valid C# name")]
    [InlineData("Godot.Node", "Hit", "int a, int a", "more than one value")]
    public void SignalsAreChecked(string? baseType, string name, string parameters, string expected)
    {
        GraphBuilder player = GraphBuilder.Type("Player");
        player.Graph.BaseType = baseType;
        var editor = new GraphEditor(player.Graph, library);
        GraphSignal signal = editor.AddSignal();
        editor.RenameSignal(signal.Id, name);
        Assert.True(editor.SetSignalParameters(signal.Id, parameters));

        GraphCompilationResult result = Compile(player, [player]);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.IsError && diagnostic.Message.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void SignalEditsAreUndoableAndKeepTheirValuesIds()
    {
        GraphBuilder player = Script("Player", "Godot.Node");
        var editor = new GraphEditor(player.Graph, library);
        GraphSignal signal = editor.AddSignal();
        editor.SetSignalParameters(signal.Id, "int damage");
        string damage = signal.Parameters[0].Id;

        Assert.True(editor.SetSignalParameters(signal.Id, "int damage, string source"));
        Assert.Equal(damage, signal.Parameters[0].Id);
        Assert.Equal("int damage, string source", GraphEditor.FormatParameters(signal.Parameters));
        Assert.False(editor.SetSignalParameters(signal.Id, "int"));
        Assert.Null(GraphEditor.ParseParameters("int damage source"));
        Assert.Equal("int damage, string source", GraphEditor.FormatParameters(GraphSerializer.Deserialize(GraphSerializer.Serialize(player.Graph)).Signals[0].Parameters));
        Assert.Equal("int damage, string source", GraphEditor.FormatParameters(player.Graph.Clone().Signals[0].Parameters));

        editor.Undo();
        Assert.Equal("int damage", GraphEditor.FormatParameters(signal.Parameters));
        editor.Undo();
        editor.Undo();
        Assert.Empty(player.Graph.Signals);
    }

    [Fact]
    public void AGraphWithoutSignalsOrSignalValuesLoadsWithEmptyLists()
    {
        string json = GraphSerializer.Serialize(GraphBuilder.Type("Player").Graph).Replace(",\n  \"signals\": []", "", StringComparison.Ordinal);
        Assert.DoesNotContain("signals", json, StringComparison.Ordinal);
        Assert.Empty(GraphSerializer.Deserialize(json).Clone().Signals);

        string withSignal = json.Replace("\"schemaVersion\"", "\"signals\": [ { \"id\": \"s\", \"name\": \"Hit\" } ], \"schemaVersion\"", StringComparison.Ordinal);
        Assert.Empty(GraphSerializer.Deserialize(withSignal).Clone().Signals[0].Parameters);
    }

    [Fact]
    public void BreakCannotLeaveAHandler()
    {
        // A While Loop whose body subscribes a handler that tries to Break out of the loop.
        GraphBuilder area = Script("Pickup", "Godot.Area2D");
        GraphBuilder ready = Method(area, "_Ready");
        string entry = ready.Node(FunctionNodes.EntryTypeId);
        string loop = ready.Node("flow.while");
        string on = ready.Node("dotnet:E:Godot.Area2D.BodyEntered");
        string jump = ready.Node("flow.break");
        ready.Then(entry, loop).Then(loop, on, "body").Then(on, jump, "handler")
            .Connect(ready.Node("literal.bool", "true"), "value", loop, "condition")
            .Connect(ready.Node(TypeNodes.SelfTypeId), "self", on, "instance");

        GraphCompilationResult result = Compile(ready, [area, ready]);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.IsError && diagnostic.NodeId == jump);
    }

    [Fact]
    public void ANewGodotProjectHasAScriptAndAScene()
    {
        using var folder = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(folder.Path, "project.godot"), "config_version=5\n");

        VisualProject project = VisualProject.CreateGodot(folder.Path, "Game");

        Assert.True(project.IsGodotProject);
        Assert.True(project.References.IsGodot);
        Assert.True(project.References.PackagesNotRestored);
        Assert.StartsWith($"<Project Sdk=\"Godot.NET.Sdk/{VisualProject.GodotVersion}\">", File.ReadAllText(project.CsprojPath), StringComparison.Ordinal);
        Assert.Equal("config_version=5\n", File.ReadAllText(Path.Combine(folder.Path, "project.godot")));
        Assert.Contains("path=\"res://Generated/Main.cs\"", File.ReadAllText(Path.Combine(folder.Path, "Main.tscn")), StringComparison.Ordinal);
        Assert.False(VisualProject.Create(Path.Combine(folder.Path, "Console"), "Console").IsGodotProject);

        VisualProject loaded = VisualProject.Load(project.ProjectFilePath);
        GraphDocument main = Assert.Single(loaded.GraphDocuments, graph => graph.Kind == GraphKind.Class);
        Assert.Equal("Godot.Node", main.BaseType);
        GraphDocument[] graphs = [.. loaded.GraphDocuments];
        GeneratedSource[] sources = [.. graphs.Select(graph =>
        {
            GraphCompilationResult result = new GraphCompiler(library).Compile(graph, new GraphCompilationOptions("Game", graphs), "Graphs/Test.graph.json");
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            return result.Source!;
        })];
        Assert.Equal(["Main.cs", "Main._Ready.g.cs"], sources.Select(source => source.RelativePath));
        Assert.Contains("public override void _Ready()", sources[1].Text, StringComparison.Ordinal);
        Assert.Contains("GD.Print(\"Hello from ScriptArt Code!\");", sources[1].Text, StringComparison.Ordinal);
    }
}
