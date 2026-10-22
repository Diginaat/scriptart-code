# Compiler pipeline

Entry point: `GraphCompiler.Compile(graph, options, sourceDescription)` in `src/VisualStudioLikeIDE.Compiler`.

```
GraphDocument
  → GraphIndex.Build        structural validation, definition resolution, connection and type checks, property validation
  → entry discovery         exactly one Entry node
  → ExecutionFlow.Analyze   reachability, cycle detection, immediate post-dominators
  → MethodLowerer           execution-ordered lowering to IR, scopes, data availability
  → IrSimplifier            readability rewrites that preserve semantics
  → CSharpEmitter           Roslyn syntax tree, NormalizeWhitespace, header, source map
  → RoslynVerifier          in-memory C# compilation (project-level, in VisualProject.Generate)
  → GeneratedFileWriter     ownership-checked, write-if-changed
```

If any stage reports an error, no C# is produced and nothing is written.

## Stages

**Structural validation (`GraphIndex`).** Resolves each node's definition, rejects duplicate node IDs, unknown types, dangling connections, output-to-output or execution-to-data links, type mismatches, multiple connections on single-connection pins (data inputs, execution outputs), and invalid property text. Invalid elements are reported and left out of the index so later stages never see them.

**Control-flow analysis (`ExecutionFlow`).** Depth-first search from the Entry node over execution connections. A back edge is an error: repetition is expressed with loop nodes, not with wires that point backwards. A loop node's Loop Body output is followed for reachability and cycle detection but is not a successor of the loop node, so the loop node continues at its Completed output and the body forms a separate region whose flow ends where an iteration ends. Immediate post-dominators are computed with the Cooper–Harvey–Kennedy algorithm on the reversed graph; a virtual exit follows every node with an unconnected execution output or none at all. All traversals are iterative.

**Lowering (`MethodLowerer`).** Walks statement nodes in execution order. After each node it continues at the node's immediate post-dominator. For a linear node that is the next node; for a Branch it is the point where both arms rejoin, so arms are lowered as nested blocks that stop there and the shared continuation follows the `if`. Each node definition lowers itself through a context:

- Data inputs from pure nodes are lowered recursively and inlined as expressions.
- Data inputs from statement nodes (for example Read Line's output) refer to the local the producer declared. Locals are visible in the declaring block and nested blocks only, mirroring C# scoping; using a value on a path where its producer did not run is an error.
- `Int32 → Double` connections insert an explicit conversion so the node's arithmetic type is preserved.
- A node reached twice (an arm jumping into the middle of another path) is reported as unstructured flow.
- A loop node lowers its body with `LowerLoopBody`, inside a scope that also holds values only the body can see, such as the For Loop index (`i`, then `i2` for nested loops). Break and Continue are errors outside a loop body. A body that runs into a node outside the loop is unstructured flow.
- Graph variables used by a Get or Set node are declared at the top of the method with their initial value, before any other local is named, so they keep the user's names. Get nodes are pure and read the variable where their value is used; Set nodes emit an assignment. Variables no node uses are not declared.

**Simplification (`IrSimplifier`).** `if (c) {} else {B}` → `if (!c) {B}` (equality operators are inverted directly; relational ones are not, because of NaN); `if (c) {A; return;} else {B}` → `if (c) {A; return;} B`; empty side-effect-free `if`s are dropped; a trailing `return;` in a void method is removed.

**Emission (`CSharpEmitter`).** Roslyn `SyntaxFactory` builds the compilation unit: usings collected from referenced types, file-scoped namespace, `internal static class`, `public static void Main()`. Parentheses are added only where precedence or associativity requires them. Literals round-trip exactly (`2.0`, `-2147483648`, escaped strings). `NormalizeWhitespace` with `\n` line endings makes the output deterministic. Every statement carries a syntax annotation with its node ID, which becomes the source map.

**Verification (`RoslynVerifier`).** Generated and handwritten files are compiled together in memory against the running .NET 10 reference set. Diagnostics in generated files are mapped back to nodes through the source map. Package and project references are not resolved here; `dotnet build` remains the authority.

## Diagnostic codes

| Code | Meaning |
| --- | --- |
| VSG0001 | Unknown node type |
| VSG0002 | Duplicate node ID |
| VSG0003 | Connection refers to a missing node or pin |
| VSG0004 | Invalid connection (direction or execution/data kind) |
| VSG0005 | Type mismatch |
| VSG0006 | Data input has more than one connection |
| VSG0007 | Execution output has more than one target |
| VSG0008 | No Entry node |
| VSG0009 | More than one Entry node |
| VSG0010 | Required input not connected |
| VSG0011 | Execution cycle |
| VSG0012 | Data cycle |
| VSG0013 | Value not available on this execution path |
| VSG0014 | Statement node not reachable from Entry (warning) |
| VSG0015 | Pure node result unused (warning) |
| VSG0016 | Invalid property value |
| VSG0017 | Unstructured execution flow |
| VSG0018 | Invalid identifier (graph or namespace name) |
| VSG0019 | Duplicate connection (warning) |
| VSG0020 | Unsupported graph kind |
| VSG0021 | Get or Set node for a variable that does not exist |
| VSG0022 | Invalid variable: name, duplicate name, type, or initial value |
| VSG0023 | Variable is set but never read (warning) |
| VSG0024 | Break or Continue outside a loop body |
| VSG0025 | Function Entry outside a function, or call node for a function that does not exist |
| VSG0026 | Function with a return type has a path that does not end at Return Value |
| VSG0027 | Invalid function signature: name, parameter name, or type |
| VSP0001 | Generated file target exists and is handwritten |
| CSxxxx | Roslyn diagnostic, passed through |

## Known limitations

- One method per graph: the entry graph becomes `Program.Main`, each function graph a `private static` method of the same `partial class Program`, in its own file. Classes are not supported yet. Variables are method-wide; there are no block-scoped variables yet.
- A pure node with inputs that one statement uses more than once is computed once into a local declared before that statement. A pure node used by several statements, or by a loop's condition, is still inlined at each use, because a variable it reads may change in between.
- Constant conditions produce Roslyn warnings (for example CS0162) rather than being folded.
