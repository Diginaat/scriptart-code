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

A loop node declares its body output with `PinDefinition.LoopBody()` and lowers it with `context.LowerLoopBody`, passing any data outputs that only exist inside the body (create their locals with `context.NewLocal`). For Each nodes (`CollectionNodes`) are loop nodes too. Other nested blocks use `PinDefinition.Block(id, name)` and `context.LowerBlock(id, kind, values)`, as the Try node does.

See `ConsoleReadLineNode`, `BranchNode`, and `ForLoopNode` in `src/VisualStudioLikeIDE.Compiler/Nodes/BuiltInNodes.cs`.

## Nodes whose pins depend on the graph

Most definitions are registered once and shared by every node of their type. Get and Set variable nodes are different: `NodeLibrary.Resolve(node, graph)` builds their definition from the variable the node refers to, so the title and pin types follow the variable. `NodeDefinition.InitialProperties` carries the variable id into new nodes. Everything that needs a node's definition (the compiler's `GraphIndex`, `GraphEditor.DefinitionOf`, the palette through `GraphEditor.AvailableDefinitions`) goes through this one method; Function Entry, Return Value, and call nodes use the same mechanism, building their pins from the function's signature.

## Rules

- Type IDs and pin IDs are persisted in graph files. Never rename them after release; add a new node and keep the old one.
- `Description` is shown in the palette tooltip and the details panel. State exceptions the generated code can throw.
- Properties are strings parsed with the invariant culture. Override `ValidateProperty` for rules beyond the property type.
- Register definitions with `NodeLibrary.Register`. The built-in set is created by `NodeLibrary.CreateDefault()`.
- Test new nodes like the built-ins: assert the generated text in `CodeGenerationTests` and, for anything with runtime behavior, add an integration test that builds and runs it.

## Third-party libraries

Loading node libraries from external assemblies is not implemented yet (Phase 5). Such libraries execute code inside the IDE with the user's permissions and must be treated as untrusted; the plan is explicit user opt-in per library, no automatic loading from opened projects.
