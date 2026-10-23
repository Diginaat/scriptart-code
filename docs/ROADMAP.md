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

Done: function graphs (graph schema version 5). File → Add Function, or **+ Function** in the Graphs list, adds a function graph; selecting a graph in the list opens it, and each graph keeps its own undo history. The Details panel edits the function's name, return type, and parameters. Function Entry outputs the parameters, Return Value returns a value, and a Call node per function appears in the palette of every graph, including the function itself for recursion. Each function generates a `static` method (`private` until methods of classes and structs, D-025) in its own `.g.cs` file. See the `Functions` sample.

Done: shared-subexpression hoisting. A value one statement uses more than once is computed once into a local before it (D-018).

Done: exceptions. The Try node runs its Try block, a Catch block with the error's Message, and a Finally block; Throw Error stops with a message (D-019). See the `Exceptions` sample.

Done: lists and `foreach`. Variables, parameters, and return values can be `List<T>` of the four simple types. Lists nodes create, add, set, get, remove, clear, count, and search; For Each loops over a list; Split Text and Join Text convert between text and lists (D-020). See the `Collections` sample.

Done: calling .NET methods. Methods of `Math`, `String`, `Convert`, `Environment`, `File`, `Path`, and `Thread`, and of every public type in a project's referenced assemblies and NuGet packages, appear in the palette under `.NET <Type>`; signatures graphs cannot express are filtered out (D-021).

Done: classes, structs, and properties (graph schema version 6). **+ Add → Class** or **Struct** in the Graphs list (or the File menu) adds a type graph whose properties, with their types and initial values, are edited in Details. A type can be used wherever a variable, parameter, return, or property type is chosen. Each type has a New node and a Get node per property in the Types category, plus a Set node per property for a class and a With node (a copy with one property changed) for a struct. Each type generates an `internal sealed class` or `internal struct` in its own `.g.cs` file (D-022). See the `Types` sample.

Done: editor layout closer to established visual scripting editors (D-023). The left panel has a **My Project** tab (collapsible Graphs, Functions, Classes, Structs, and Variables sections with **+** buttons, **+ Add New**, and search) and a **Palette** tab. Right-clicking empty space opens the action menu: nodes grouped by category with search and a **Context Sensitive** toggle; releasing a wire on empty space opens the same menu. Variables are selected in My Project, edited in Details, and dragged onto the graph for Get (Ctrl) or Set (Alt) nodes.

Done: dictionaries and lists of classes and structs (D-024). Variables, parameters, return values, and properties can be a `List<T>` or a `Dictionary<TKey, TValue>` of any simple type or project class or struct, with `int` or `string` keys. Dictionaries have Create, Set Entry, Remove Entry, Clear, Get Value (with a Default for a missing key, so it never fails), Contains Key, Count, Keys, and Values nodes in the Dictionaries category; Keys and Values give lists for For Each. The list and dictionary nodes of a type appear once the project uses that type. Chained With nodes generate `p with { X = 1 } with { Y = 2 }` without parentheses. See the `Dictionaries` sample.

Done: methods of classes and structs (graph schema version 7, D-025). **Add Method** in a class's or struct's Details adds a method graph, listed under its type in My Project and stored as `Graphs/{Type}.{Method}.graph.json`. A method has parameters and a return type like a function, a **Self** node for the object or value it runs on, and a Call node with a Target input in every graph. Each method generates a public instance method in `{Type}.{Method}.g.cs`, part of the now `partial` type; functions of the program class become `public static` so methods can call them as `Program.Name(…)`. See the `Methods` sample.

Done: constructors and generic types (graph schema version 8, D-026, D-027). **Add Constructor** in a class's or struct's Details adds a constructor graph that sets properties through Self; a New node can run it, and a parameterless constructor is generated when the type still needs one. **Type parameters** in Details make a class or struct generic (`Box<T>`); its nodes and its methods' calls take type arguments. Collections nest (`List<List<int>>`, `Dictionary<string, List<Point>>`).

Done: typed catch and Rethrow (D-028). The Try node's Catch output handles a chosen exception type; Rethrow inside Catch generates `throw;`, and Throw Error throws a chosen type.

Done: async/await (D-029). A node that calls an async .NET method waits for it with `await`, and the graph's method becomes `async Task`, as do its callers up to `Main`.

Done: constructors, properties, and generic methods of .NET types (D-030). Variables can hold `DateTime`, `TimeSpan`, `Stopwatch`, `Random`, and `StringBuilder`, whose constructors, properties, and instance methods become nodes; generic methods such as `Enumerable.First<T>` take their type arguments from the node.

Done: editor quality of life (D-031). Graphs and variables are renamed in My Project by double-click, F2, or a right-click menu, which also deletes them (with confirmation for graphs) and adds methods and constructors to types; renamed graphs move to their new file name on save. Double-clicking a Call node opens its function, a variable node or Function Entry renames it. Nodes show a hover card with what they do, what they take and give, and what double-clicking does, and the built-in nodes have plain-language descriptions with examples. Text values get a larger multi-line editor with a character count, and fixed choices are picked from a list. Node search understands everyday words (if, print, loop), initials, and typos, and describes the selected node in the action menu.

Remaining: overloaded methods and constructors, static members and inheritance of project types, and generic methods of project types.

## Phase 4 — Code quality and IDE integration ✅

Done: multiple graphs per project and a project explorer (My Project, Phase 3), now with **Files** and **References** sections.

Done: editable handwritten files (D-032). My Project → Files lists the project's handwritten `.cs` files; clicking one edits it in the **Code** tab, where it is checked with Roslyn as you type (errors in Problems, which opens the file at the line) and saved with the project (Ctrl+S). Project → Add C# File creates a class file. Generated code stays read-only; **Show Generated Code** returns to it.

Done: diff preview before regeneration (D-033). With Build → Preview Changes Before Generating (on by default), F6 lists the generated files that would be added, changed, or deleted, each as a colored unified diff, and writes nothing until **Write Files**. `vside diff` prints the same diff.

Done: Go to Definition (D-034). F12 on a node opens the function, method, constructor, type, or variable it uses; F12 in code goes to the symbol's declaration: a node of a graph for generated code, the line of a handwritten file, or a message for .NET symbols.

Done: debugging (D-035). F9 sets a breakpoint on a node; F5 builds and runs under netcoredbg, the open-source .NET debugger, over the Debug Adapter Protocol. A stop outlines the node on its graph (or opens the handwritten file at the line) and fills the **Debug** tab with the call stack and expandable locals. Continue (F5), Step Over (F10), Step Into (F11), Step Out (Shift+F11), Pause, and Stop; program input works while debugging. Ctrl+F5 runs without the debugger.

Done: solution management (D-036). Project → Create Solution File (`dotnet new sln`, `.slnx`), Add NuGet Package, Add Project Reference (also added to the solution), and removing either from References, all through the `dotnet` CLI with output in Output. A referenced project's public methods become nodes after a build.

## Phase 5 — Extensibility and performance ✅

Done: node-library SDK and opt-in libraries (D-038). `ScriptArt.NodeSdk` holds what a node needs; a library implements `INodeProvider`. Tools → Node Libraries approves a library for the user after a warning that it runs with their permissions; the CLI takes `--node-library`. Opening a project never loads one.

Done: schema migration tooling. `vside migrate --check` lists files in an older format and the steps that would convert them; `vside migrate` converts all of them after backing them up to `MigrationBackup/`, or none if one cannot be read. The IDE notes outdated files in Output when a project opens.

Done: incremental compilation (D-039). Only changed graphs recompile; a signature change recompiles all.

Done: spatial index and background compilation (D-040, D-041). The canvas culls and hit-tests through a grid index; live compilation runs off the UI thread on snapshots.

Done: performance regression tests. The performance harness compares against `tests/ScriptArt.PerformanceTests/baseline.json` with `--check`.

Done: packaging. `eng/package.sh` / `eng/package.ps1` publish the IDE self-contained per runtime and pack the CLI as a .NET tool and the SDK as NuGet packages.

Remaining: unloading a removed library without a restart, a signed-package feed for libraries.

Next: Phase 6. Godot 4 C# support is the last phase.

## Phase 6 — Production readiness

Goal: a release that people other than the developers can install, trust with their work, and upgrade. Released so far: `alpha-v0.1.0` (tag only; artifacts built by `eng/package.sh`).

Starting point (2026-10-23): no installer, no unhandled-exception handling (`Program.Main` starts Avalonia directly), no autosave, no preferences store (only `node-libraries.json` in `%AppData%/ScriptArtCode`), no automation peers for the canvas, no license file.

Work, in order; each item ends with its tests and docs:

1. **Preferences** (first, because later items store settings in it). A versioned `preferences.json` beside `node-libraries.json`, read and written by ProjectSystem (no Avalonia types), with migration like the graph format. Initial settings: recent projects, autosave interval, preview-before-generate, netcoredbg path, theme, UI scale. Tools → Preferences edits them. Unknown or invalid values fall back to defaults and are reported, never crash.
2. **Crash handling and logs.** Catch unhandled exceptions on the UI thread, the thread pool (`TaskScheduler.UnobservedTaskException`), and `AppDomain`; write a crash report (version, OS, stack, recent log) to `%LocalAppData%/ScriptArtCode/logs`, offer to save recovery copies, and show where the report is. A rolling log file replaces `LogToTrace` for diagnostics. No telemetry: nothing leaves the machine.
3. **Autosave and recovery.** Autosave writes recovery copies of dirty graphs and handwritten files to `%LocalAppData%/ScriptArtCode/recovery/<project hash>/`, never over the user's files. On open, newer recovery copies are offered as a diff (reusing the generation diff view) to restore or discard. Saving deletes them. Interval from preferences.
4. **Accessibility review.** Keyboard reachability of every command (audit menus, panels, overlays), visible focus, contrast of the theme (WCAG AA), UI scale. The canvas gets an automation peer that exposes nodes, pins, and connections to screen readers (Narrator, Orca), with the existing keyboard connecting as the way to edit. Findings and what was fixed go in `docs/ACCESSIBILITY.md`.
5. **Installer and updates.** Per-user install without admin rights, Start-menu entry, file association for `.vside`, uninstall that keeps projects and settings. Update check that only reads a release feed and asks before downloading. Signed binaries where a certificate is available.
6. **Upgrade testing.** Fixtures of every released schema (graph 1–8, project 1, `node-libraries.json` 1, `preferences.json` 1) opened, saved, and compared in tests; an integration test that opens each sample generated by the previous release. A rule in AGENTS.md: a release adds its formats to the fixtures.
7. **Security review.** Threat model in `docs/SECURITY.md`: opening untrusted projects (paths, ownership headers, MSBuild only on explicit build), node libraries (D-038), process launching (argument lists, no shell), file writes (atomic, ownership), the debugger hook, the update channel. Fix findings; add a vulnerability-reporting policy.
8. **Release checklist.** `docs/RELEASING.md`: version bump, all test suites and the performance check, regenerate samples, package all runtimes, smoke-start each package, tag, release notes, upload, verify the update feed. A CI workflow (GitHub Actions) runs unit, integration, and packaging on Windows, Linux, and macOS for every push and builds release artifacts from tags.

Done when: a fresh machine installs the release without the .NET SDK, opens and edits a sample, survives a forced kill without losing more than the autosave interval, upgrades to a newer build keeping settings, and every item above has tests or a recorded manual check.

Decided (D-042): MIT license; Velopack for installers and updates on Windows, macOS, and Linux, fed from GitHub Releases; unsigned binaries for now, with the SmartScreen and Gatekeeper warnings documented.

## Phase 7 — Godot 4 C# support (last)

Goal: build Godot 4 games with graphs. A project can target a Godot 4 .NET project (`project.godot` plus a `Godot.NET.Sdk` `.csproj`) instead of a console application.

- Project template: create or open a Godot 4 C# project; the `.csproj` keeps `Godot.NET.Sdk` and the generated files land inside the Godot project so the editor and `dotnet build` both pick them up.
- Script graphs: a class graph can extend a Godot type (`Node`, `Node2D`, `CharacterBody2D`, …) and generates a `partial` class, as Godot's source generators require. Event graphs for the virtual methods (`_Ready`, `_Process(double delta)`, `_PhysicsProcess`, `_Input`) and `[Export]` properties edited in Details.
- Godot API nodes: the members of the GodotSharp assembly through the existing .NET discovery (D-021), with `Vector2`, `Vector3`, `Node` paths (`GetNode<T>`), signals (`[Signal]` delegates, `Connect`, `EmitSignal`), and `GD.Print`.
- Godot 4 C# language server: start a language server (LSP) over the Godot project for completion, hover, go to definition, and diagnostics in handwritten and generated C#, and map its diagnostics onto nodes through the source map. Connect to Godot's own editor features where they exist (opening scripts, running the game).
- Run: launch the game with the Godot 4 .NET executable configured in preferences, and show its output in the Output panel.
- A Godot sample (a moving 2D character) built and checked like the other samples; integration tests that need the Godot executable are skipped when it is not installed.

Open questions: which language server to host (Roslyn-based C# LSP or OmniSharp) and how it is shipped; the minimum Godot 4.x version.

## Known limitations of the current build

- Deleting a graph cannot be undone. Variables, parameters, return types, and properties are limited to `bool`, `int`, `double`, `string`, the project's classes and structs, and lists and dictionaries (with `int` or `string` keys) of these; collections do not nest. Variables are method-wide.
- Variables, parameters, and properties refer to a class or struct by name, so renaming the type leaves them reporting an unsupported type until they are changed to the new name.
- Reroute points stay in place when the nodes they connect, or a comment around them, are moved.
- Running programs receive input from the Output panel's input box; there is no interactive terminal.
- The `.csproj` is created once; build settings are edited there directly.
