# Roadmap

## Phase 0 — Research and technical design ✅

.NET 10 SDK 10.0.401 verified. Avalonia 12.1.4, AvaloniaEdit 12.0.0, Roslyn 5.0.0, xUnit v3 selected (all MIT/Apache-2.0, current). Decisions in [DECISIONS.md](DECISIONS.md); schema in [GRAPH_FORMAT.md](GRAPH_FORMAT.md); pipeline in [COMPILER_PIPELINE.md](COMPILER_PIPELINE.md); ownership in [CODE_GENERATION.md](CODE_GENERATION.md).

## Phase 1 — Minimal end-to-end slice ✅

Create/open project, edit the entry graph, add and connect nodes, validate, generate readable C#, save `.cs`, build and run with the .NET SDK from the IDE and the CLI. Console applications only.

## Phase 2 — Core visual editor ✅ (dockable panels on hold)

Already in place: typed pins, connection validation feedback, undo/redo, serialization, diagnostics on nodes, dark theme, keyboard selection cycling, context-sensitive node creation (release a wire on empty space to pick from compatible nodes, added and connected as one undo step), drag-and-drop from the palette, cut/copy/paste/duplicate through the system clipboard, inline value editing on nodes, comments that also act as groups (graph schema version 2), reroute points on wires (version 3), minimap, align and distribute, canvas frame statistics (View → Frame Statistics) with the first render optimizations, command palette (Ctrl+Shift+P), keyboard connection of pins (Ctrl+L with two nodes selected).

On hold: **dockable panels**. Chosen approach: Dock.Avalonia 12.1.0.12 (MIT, built for Avalonia 12.1.4). Its panel content is a template by default, which would break MainWindow's named controls and lose panel state, but it also accepts a ready-made control as content. The plan is to keep the existing panels as named controls and build the dock layout in code around them.

## Phase 3 — Real programming capabilities (in progress)

Done: local variables (graph schema version 4): declared in the Details panel with a name, type, and initial value; Get and Set nodes per variable; generated as typed locals and assignments. See the `Variables` sample.

Done: loops. While Loop and For Loop nodes have a Loop Body output and a Completed output; Break and Continue nodes end an iteration. They generate `while`, `for`, `break`, and `continue`. See the `Loops` sample. `foreach` waits for collection types.

Done: function graphs (graph schema version 5). File → Add Function, or **+ Function** in the Graphs list, adds a function graph; selecting a graph in the list opens it, and each graph keeps its own undo history. The Details panel edits the function's name, return type, and parameters. Function Entry outputs the parameters, Return Value returns a value, and a Call node per function appears in the palette of every graph, including the function itself for recursion. Each function generates a `private static` method in its own `.g.cs` file. See the `Functions` sample.

Done: shared-subexpression hoisting. A value one statement uses more than once is computed once into a local before it (D-018).

Done: exceptions. The Try node runs its Try block, a Catch block with the error's Message, and a Finally block; Throw Error stops with a message (D-019). See the `Exceptions` sample.

Done: lists and `foreach`. Variables, parameters, and return values can be `List<T>` of the four simple types. Lists nodes create, add, set, get, remove, clear, count, and search; For Each loops over a list; Split Text and Join Text convert between text and lists (D-020). See the `Collections` sample.

Done: calling .NET methods. Methods of `Math`, `String`, `Convert`, `Environment`, `File`, `Path`, and `Thread`, and of every public type in a project's referenced assemblies and NuGet packages, appear in the palette under `.NET <Type>`; signatures graphs cannot express are filtered out (D-021).

Done: classes, structs, and properties (graph schema version 6). **+ Add → Class** or **Struct** in the Graphs list (or the File menu) adds a type graph whose properties, with their types and initial values, are edited in Details. A type can be used wherever a variable, parameter, return, or property type is chosen. Each type has a New node and a Get node per property in the Types category, plus a Set node per property for a class and a With node (a copy with one property changed) for a struct. Each type generates an `internal sealed class` or `internal struct` in its own `.g.cs` file (D-022). See the `Types` sample.

Done: editor layout closer to established visual scripting editors (D-023). The left panel has a **My Project** tab (collapsible Graphs, Functions, Classes, Structs, and Variables sections with **+** buttons, **+ Add New**, and search) and a **Palette** tab. Right-clicking empty space opens the action menu: nodes grouped by category with search and a **Context Sensitive** toggle; releasing a wire on empty space opens the same menu. Variables are selected in My Project, edited in Details, and dragged onto the graph for Get (Ctrl) or Set (Alt) nodes.

Next: dictionaries and lists of classes and structs.

Remaining: dictionaries, lists of classes and structs, nested and user-defined generic types; methods and constructors on classes and structs; nested and user-defined generic types; constructors, properties, and generic methods of .NET types; typed catch clauses and rethrow; async/await.

## Phase 4 — Code quality and IDE integration

Multiple graphs per project and a project explorer; editable handwritten files in the code editor; diff preview before regeneration; Go to definition; debugging through the standard .NET debugger with node breakpoints from the source map; solution management.

## Phase 5 — Extensibility and performance

Node-library SDK assembly and opt-in loading of external libraries; schema migration tooling; incremental compilation; spatial indexing and background compilation; performance regression tests; packaging.

## Phase 6 — Production readiness

Installer, crash recovery and autosave, accessibility review, preferences, upgrade testing, security review, release checklist.

## Known limitations of the current build

- Function, class, and struct graphs cannot be deleted or renamed on disk from the IDE yet; the file keeps its first name. Variables, parameters, return types, and properties are limited to `bool`, `int`, `double`, `string`, lists of them, and the project's classes and structs. Variables are method-wide.
- Variables, parameters, and properties refer to a class or struct by name, so renaming the type leaves them reporting an unsupported type until they are changed to the new name.
- Reroute points stay in place when the nodes they connect, or a comment around them, are moved.
- Running programs receive input from the Output panel's input box; there is no interactive terminal.
- The `.csproj` is created once; build settings are edited there directly.
