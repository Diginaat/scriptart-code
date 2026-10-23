# Authoring nodes

A node is a `NodeDefinition`: a stable type ID, display metadata, pins, optional editable properties, and a lowering method that produces IR. Definitions never produce C# text or touch Roslyn; the emitter owns formatting.

## Pure nodes

No execution pins, exactly one data output, no side effects. The expression is inlined wherever the output is used.

```csharp
public sealed class MaxNode() : PureNodeDefinition(
    "math.int.max",
    "Max (Integer)",
    "Math",
    "Returns the larger of A and B.",
    [
        PinDefinition.DataIn("a", "A", TypeRef.Int32),
        PinDefinition.DataIn("b", "B", TypeRef.Int32),
        PinDefinition.DataOut("result", "Result", TypeRef.Int32),
    ])
{
    private static readonly TypeRef MathType = new("System", "Math");

    public override IrExpression Lower(ExpressionLoweringContext context) =>
        new IrStaticCall(MathType, "Max", [context.Input("a"), context.Input("b")], TypeRef.Int32);
}
```

## Statement nodes

Run in execution order. Emit statements with `context.Emit`, expose values with `context.DeclareOutput` (which creates a local only when the output is connected), and lower nested execution outputs with `context.LowerBranch`. Do not lower the node's own continuation: the compiler continues at the point where the node's outgoing paths rejoin.

A loop node declares its body output with `PinDefinition.LoopBody()` and lowers it with `context.LowerLoopBody`, passing any data outputs that only exist inside the body (create their locals with `context.NewLocal`). Nodes for .NET methods are not written by hand: `ExternalApi` builds them from assembly metadata (`ExternalMethodNode`, `ExternalPureMethodNode`), and `NodeLibrary.CreateDefault(referencePaths)` registers them. For Each nodes (`CollectionNodes`) are loop nodes too. Other nested blocks use `PinDefinition.Block(id, name)` and `context.LowerBlock(id, kind, values)`, as the Try node does.

See `ConsoleReadLineNode`, `BranchNode`, and `ForLoopNode` in `src/ScriptArt.Compiler/Nodes/BuiltInNodes.cs`.

## Nodes whose pins depend on the graph

Most definitions are registered once and shared by every node of their type. Get and Set variable nodes are different: `NodeLibrary.Resolve(node, graph)` builds their definition from the variable the node refers to, so the title and pin types follow the variable. `NodeDefinition.InitialProperties` carries the variable id into new nodes. Everything that needs a node's definition (the compiler's `GraphIndex`, `GraphEditor.DefinitionOf`, the palette through `GraphEditor.AvailableDefinitions`) goes through this one method; Function Entry, Return Value, and call nodes use the same mechanism, building their pins from the function's signature, and so do the New, Get, Set, and With nodes of classes and structs (`TypeNodes`), building theirs from the type graph and property they name.

## Rules

- Type IDs and pin IDs are persisted in graph files. Never rename them after release; add a new node and keep the old one.
- `Description` is shown in the palette tooltip and the details panel. State exceptions the generated code can throw.
- Properties are strings parsed with the invariant culture. Override `ValidateProperty` for rules beyond the property type.
- Register definitions with `NodeLibrary.Register`. The built-in set is created by `NodeLibrary.CreateDefault()`.
- Test new nodes like the built-ins: assert the generated text in `CodeGenerationTests` and, for anything with runtime behavior, add an integration test that builds and runs it.

## Node libraries

A node library is a class library that references the `ScriptArt.NodeSdk` package (which brings `ScriptArt.GraphModel`) and contains one or more public classes implementing `INodeProvider`:

```csharp
public sealed class Shout() : PureNodeDefinition("acme.shout", "Shout", "Acme", "Adds an exclamation mark.",
    [PinDefinition.DataIn("text", "Text", TypeRef.String), PinDefinition.DataOut("result", "Result", TypeRef.String)])
{
    public override IrExpression Lower(ExpressionLoweringContext context) =>
        new IrStaticCall(TypeRef.String, "Concat", [context.Input("text"), new IrLiteral("!", TypeRef.String)], TypeRef.String);
}

public sealed class AcmeNodes : INodeProvider
{
    public IEnumerable<NodeDefinition> CreateNodes() => [new Shout()];
}
```

- Prefix type IDs with your own name (`acme.`). A node whose type ID is already in use is skipped and reported; the built-in node keeps it.
- Target the same SDK version as the IDE. The SDK and graph model are shared with the IDE; other dependencies load beside your assembly from its folder (its `.deps.json` is honored).
- `CreateNodes` runs when the library loads. Keep it fast and free of side effects; an exception is reported and the library adds no nodes.

Loading a library runs its code with the user's permissions (D-038). The IDE loads only libraries approved in **Tools → Node Libraries**, which warns before approving and records the file's SHA-256 for the current user; a library whose file changes is not loaded until approved again, and opening a project never loads one. From the command line, name each library: `vside build --node-library Acme.Nodes.dll MyApp.vside`. A project that uses a library's nodes needs that library wherever it is generated; the generated C# does not need it.
