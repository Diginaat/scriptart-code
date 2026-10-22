# Architectural decisions

## D-001 Avalonia 12 for the desktop UI

**Decision.** Avalonia 12.1.4 (MIT) with the Fluent dark theme. **Why.** Cross-platform desktop on .NET 10, mature, actively maintained, with a retained control tree for panels and an immediate-mode `DrawingContext` for custom surfaces. **Alternatives.** WPF/WinUI (Windows-only); MAUI (weak desktop tooling); a game-engine renderer (unjustified dependency).

## D-002 Custom canvas on Avalonia's DrawingContext

**Decision.** The node canvas is one `Control` that draws nodes, pins, and wires itself, with view-only layout objects, cached text, viewport culling, and no per-node controls. **Why.** Thousands of nodes as individual controls would make layout and hit-testing the bottleneck; a single drawing surface keeps the cost proportional to what is visible. A separate GPU layer is not justified until measurements show the DrawingContext path is the bottleneck (see PERFORMANCE.md).

## D-003 Roslyn for emission and verification

**Decision.** Microsoft.CodeAnalysis.CSharp 5.0.0 (MIT): `SyntaxFactory` to build code, `NormalizeWhitespace` to format, in-memory `CSharpCompilation` to verify. **Why.** Structurally correct output by construction, deterministic formatting, and real C# diagnostics before writing files. The Workspaces formatter was not used: it adds MEF and workspace dependencies for little benefit at this stage.

## D-004 Few assemblies

**Decision.** Six source projects instead of the fifteen originally sketched. Type system, diagnostics, node library, IR, and emission are folders in the compiler; serialization is in the graph model; build integration is in the project system. **Why.** Each split should serve a real consumer. A node-library SDK assembly will be extracted when external libraries are supported.

## D-005 IR between graph and syntax

**Decision.** Node definitions lower to a small IR (`IrExpression`, `IrStatement`); only the emitter knows Roslyn. **Why.** Keeps node authoring independent of Roslyn, allows simplification passes, and lets new targets or formatting change without touching nodes.

## D-006 Structured control flow from post-dominators

**Decision.** Execution graphs are structured into nested blocks using immediate post-dominators; unstructured shapes are rejected (VSG0017) rather than compiled with `goto` or a state machine. **Why.** The product promise is readable C#. Rejecting rare unstructured graphs with a clear message is better than emitting code nobody would write.

## D-007 Pure nodes are inlined

**Decision.** Side-effect-free nodes are inlined at each use; statement-node outputs become locals. **Why.** Produces the most natural code for typical graphs (`"Hello, " + line + "!"`). Shared-subexpression hoisting is a planned optimization.

## D-008 Ownership by file header

**Decision.** Generated files live in `Generated/` and carry a fixed header; the writer only overwrites or deletes files that carry it. The `.csproj` is created once and then belongs to the user. **Why.** Never silently destroy handwritten work, while keeping the project buildable with plain `dotnet build`.

## D-009 Project file extension `.vside`

**Decision.** Not `.vgproj`. **Why.** MSBuild treats any `*.*proj` in a folder as a project, which made `dotnet build` in a generated project folder fail with MSB1011. Found by building a sample with the plain CLI.

## D-010 AvaloniaEdit for code views

**Decision.** Avalonia.AvaloniaEdit 12.0.0 (MIT) for the generated-code and output views, with the built-in C# definition recolored for the dark theme. **Why.** A real editor component with line numbers, selection, search, and efficient appends; TextMate grammars can replace the built-in definition later.

## D-011 Comments are the grouping mechanism

**Decision.** A comment is a titled box stored in the graph (`comments`, schema version 2). Dragging its title moves every node and comment lying completely inside it. Membership is derived from geometry at drag time and never stored. **Why.** One concept covers both documentation and grouping, as in Unreal Blueprints; storing membership would need bookkeeping whenever a node moves in or out. Comments are ignored by the compiler, so they cannot change generated code.

## D-012 Reroute points are wire layout, not nodes

**Decision.** A reroute point is a position stored on the connection (`points`, schema version 3). Connection equality ignores points. **Why.** Points never affect meaning, so the compiler, connection rules, and diagnostics stay untouched. Reroute nodes, as in Unreal Blueprints, would need pass-through pins whose type depends on what is connected, which the type system does not have. Trade-off: one knot cannot fan out to several inputs; each wire is rerouted separately.

## D-013 xUnit v3

**Decision.** xunit.v3 3.2.2 with Microsoft.NET.Test.Sdk. Integration tests launch the real `dotnet` CLI instead of mocking the build.

## D-014 The command palette is built from the menu

**Decision.** The command palette lists the main menu's enabled leaf items (plus one "Add Node" entry per node) and runs them by raising the item's `Click`. **Why.** The menu stays the single list of commands, so a command added to the menu appears in the palette with its shortcut and enabled state, and there is no second registry to keep in sync. Trade-off: a command must have a menu item to be searchable. The same overlay lists the connections between two selected nodes for keyboard connecting (Ctrl+L); the list comes from `GraphEditor.PossibleConnections`, which applies the same rules as dragging a wire.

## D-015 Variables are declared on the graph; their nodes are resolved per node

**Decision.** A graph lists its variables (`variables`, schema version 4). Get and Set nodes store only the variable's id, and `NodeLibrary.Resolve` builds their definition from the variable. **Why.** A node's pins must follow the variable's current name and type; storing pins in the file, or registering one node type per variable, would let them drift apart when a variable is renamed or retyped. Referring by id keeps renames free. Changing a type removes the wires that no longer fit, in the same undo step. The same per-node resolution is what function call nodes need. Trade-off: copying a variable node into a graph that lacks the variable leaves a node that reports VSG0021 until it is deleted.

## D-016 Loops are nodes with a body output, not backward wires

**Decision.** Repetition uses While Loop and For Loop nodes. Their Loop Body output starts a nested region; the iteration ends where that flow ends. Wires pointing back to an earlier node stay an error. **Why.** Backward wires allow arbitrary gotos that have no structured C# form, and recovering loops from them would make the generated code depend on graph shape heuristics. A body region maps one-to-one onto a C# block, keeps the existing post-dominator lowering unchanged, and gives locals declared in the body the same scope they have in C#. Trade-off: a loop whose exit is in the middle of the body needs a Branch and a Break node.

## D-017 Functions are separate graphs in one partial class

**Decision.** A function is its own graph file (`kind: Function`, schema version 5) with parameters and an optional return type. It generates a `private static` method in its own `.g.cs` file, as part of `partial class Program`. Call nodes store only the function's graph id and are resolved per node, like variable nodes. A call is always a statement node with execution pins. **Why.** One graph per method keeps each canvas readable and maps one-to-one onto generated files and source maps. A partial class lets each graph compile to its own file without a merge step. Resolving by id keeps calls intact when a function or parameter is renamed. Calls run in execution order because a function may have side effects; a pure call node would be inlined at every use and run more than once. A function with a return type must end every path at Return Value (VSG0026), so the generated method always compiles. Trade-off: removing a parameter leaves wires on call nodes in other graphs, which the compiler reports until they are deleted.

## D-018 Shared pure values are hoisted per statement

**Decision.** When one statement uses a pure node with inputs more than once, the value is computed once into a local declared just before that statement. Values shared between statements, and loop inputs (While condition, For end), stay inline. **Why.** Within one statement nothing runs between the uses, so one evaluation is equivalent and the code reads like hand-written C#. Between statements a Set node may change a variable the value reads, and a loop condition must be evaluated on every iteration; hoisting there would change behavior. Literals and variable reads stay inline because a local would add nothing. Trade-off: a value shared by two statements is still computed twice.

## D-019 Exceptions use one Try node with nested blocks

**Decision.** A Try node has Try, Catch, and Finally outputs that start nested blocks, like a loop body, and a Completed output. Catch handles every `Exception` and offers its Message as a string output. Throw Error throws `InvalidOperationException` with a message. **Why.** Nested blocks map one-to-one onto `try`/`catch`/`finally` and reuse the region lowering that loops already have (`PinDefinition.Block`). Exception types other than `Exception` and `string` messages would need the type system to know exception classes, which waits for external .NET types. Jumps out of Finally are reported as VSG0028 instead of being left to Roslyn (CS0157), so the error points at the node. Trade-off: no typed catch clauses, no rethrow, and no exception filters yet.

## D-020 Lists are the one generic type; list nodes are registered per element type

**Decision.** `TypeRef` has an optional `ElementType`, used for `List<T>` of `bool`, `int`, `double`, or `string`. Variables, parameters, and return values can be lists; their type is stored by C# name (`List<int>`), so the file format needs no new fields. List nodes (`list.int.add`, `flow.foreach.string`, …) are registered once per element type. Create List is a statement node. **Why.** One element type parameter covers the collections programs need first, and keeps `TypeRef` a record with value equality. Pins have fixed types, and generic pins resolved from connections would touch the editor, the connection rules, and the compiler at once; per-type registration follows the existing arithmetic nodes. Creating a list must happen once per run, but pure nodes are inlined at every use, so creation is a statement. An older build reports a list variable as an unsupported type instead of misreading it, so the schema version is unchanged. Trade-off: four copies of each list node in the palette, and no nested lists or dictionaries yet.

## D-021 .NET methods are discovered with Roslyn and named by documentation ID

**Decision.** `ExternalApi` loads assemblies as Roslyn metadata references and offers each public, ordinary, non-generic, non-obsolete method of a public top-level type whose parameters (no ref, out, in, or params) and return type map to graph types. Instance methods are offered only on types graphs have values of (`string`). The node type ID is `dotnet:` plus the documentation comment ID, for example `dotnet:M:System.Math.Max(System.Int32,System.Int32)`. Framework methods are limited to an allow-list of types (`Math`, `String`, `Convert`, `Environment`, `File`, `Path`, `Thread`); a project's `<Reference HintPath>` assemblies and restored NuGet compile assets (`obj/project.assets.json`) contribute every public type. Methods of `Math` and `String` are pure; all others are statement nodes. A nullable string or list result is coalesced to an empty value. **Why.** Roslyn is already a dependency and sees exactly what the C# compiler will bind, including nullable annotations; reflection would load code into the IDE. Documentation IDs are stable, unique per overload, and readable in the file. Treating unknown methods as statements keeps side effects in execution order. The allow-list keeps the palette usable; the whole framework is thousands of methods. A graph whose package is missing keeps its nodes and reports VSG0001, as for any unknown node type. Trade-off: no constructors, properties, events, or generic methods yet; packages are known only after restore, which the CLI runs when needed and the IDE runs with every build.
