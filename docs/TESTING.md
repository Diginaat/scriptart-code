# Testing

```sh
dotnet test tests/ScriptArt.Tests              # fast: no processes, ~2 s
dotnet test tests/ScriptArt.IntegrationTests   # real dotnet build/run, ~35 s; set VSIDE_NETCOREDBG to run the debugger test
dotnet run -c Release --project tests/ScriptArt.PerformanceTests -- --check tests/ScriptArt.PerformanceTests/baseline.json
```

The performance check compares against a baseline recorded on one machine; see PERFORMANCE.md before relying on it elsewhere.

## Suites

| Suite | Covers |
| --- | --- |
| `CodeGenerationTests` | Exact generated text for representative graphs (hello world, rejoining branch, early return, inverted condition, precedence, literals, escaping, conversions, unique locals, determinism, source map, 5,000-node chain). Every generated file is also compiled with Roslyn and must have no diagnostics. |
| `DiagnosticTests` | Each validation rule: missing/multiple entry, unconnected input, type mismatch, kind mismatch, cycles, unavailable values, invalid literals, unknown types, unreachable/unused warnings, invalid names. |
| `SerializationTests` | Round-trips, determinism, LF endings, the literal version-1 fixture and its migration to the current version, comments, reroute points, malformed and future-version files, atomic writes. |
| `ProjectSystemTests` | Project creation and loading, refusal to overwrite, ownership conflicts, stale generated file removal, write-if-changed, renamed graphs moving to their new file (and methods following their type), removing a type with its members, Roslyn errors in handwritten code, path traversal. |
| `GraphEditorTests` | Undo/redo of every operation, connection rules and replacement, compatible-pin lookup for context-sensitive creation, add-and-connect as one step, copy fragments and paste (new IDs, placement, dangling connections), comments (add, rename, resize, move with contents, delete, copy/paste), reroute points (add, move, remove, identity, paste offset), align and distribute (`LayoutToolsTests`), command palette search ranking (`SearchMatcherTests`), node search with synonyms, initials, and typos (`NodeSearchTests`), possible connections between two nodes and single-wire disconnect, variables (generated code, diagnostics, per-node pins, editor operations and undo, schema migration) in `VariableTests`, loops (while, for, nesting, break/continue, scoping and flow errors) in `LoopTests`, dirty tracking. |
| `TypeTests` | Generated classes and structs (initializers only where needed, struct constructor), New/Get/Set/With nodes in generated code, types as parameters and return values, palette contents, self-containing types, invalid properties, name clashes, missing properties, property editing with undo. |
| `DictionaryTests` | Type names of lists and dictionaries read back, unsupported names rejected, dictionary nodes in generated code, lists and dictionaries of classes and structs, collection nodes offered for the types in use, missing collection types reported on the node, dictionaries start empty. |
| `MethodTests` | Class and struct methods with Self, calls with a target, functions called from methods through the program class, file names per type, member-name clashes, methods of missing types, Self offered only in methods, saving the owner, adding a method to a project. |
| `ConstructorAndGenericTests` | Class and struct constructors through Self with a generated parameterless constructor, constructors with the same parameter types reported, generic classes with type arguments on nodes and calls, nested type names, lists of lists, adding a constructor to a project, editing type parameters. |
| `AsyncAndDotNetMemberTests` | Typed catch and Rethrow, Rethrow outside Catch reported, Throw with a chosen type, .NET constructors, properties, and instance methods of framework value types, generic .NET methods with type arguments, async calls that make their function and its callers async, optional parameters left out. |
| `NodeLibraryTests` | A node library compiled with Roslyn at test time, loaded, registered, and generating code; duplicate type IDs skipped; throwing, empty, non-assembly, and missing libraries reported; approval by hash, a changed file refused, removal. |
| `MigrationTests` | The migration steps cover every version in order; `ProjectMigration` lists and converts outdated files with a backup, changes nothing when one file is unreadable, and reports files from a newer version. |
| `IncrementalCompilationTests` | Unchanged graphs reused and edited ones recompiled; signature and async changes recompile every graph; incremental results equal a cold compile; snapshots are independent and read-only; `Clone` shares nothing mutable; metadata references cached. `SpatialIndexTests`: queries after inserts, moves, and removals match a linear scan. |
| `TextDiffTests` | Line diff (prefix and suffix trimming, insertions, deletions, replacements), unified diff hunks and context, empty files. |
| `CodeWorkflowTests` | Generation preview (added, modified, deleted, nothing written, ownership conflicts), adding and saving handwritten files and their protections, verification with unsaved text, Go to Definition in generated and handwritten code, the debugger startup hook. |
| `DebuggingTests` | DAP message framing, requests, events, and reverse requests over pipes, node breakpoints to lines and back, output assembly path. |
| `SolutionTests` | Finding a solution, package id and version validation, refused project references, packages and project references read from the `.csproj`. |
| `NodeNavigationTests` | Go to Definition targets of Call, New, property, Self, and variable nodes, and nodes without one. |
| `DebuggingAndSolutionTests` (integration) | A real debug session of the Variables sample stopping at a node breakpoint with its locals (skipped without netcoredbg); a solution with a project reference whose methods become nodes. |
| `EndToEndTests` (integration) | New project builds and prints; the ConsoleApplication sample with and without input; the Variables, Loops, Functions, Exceptions, Collections, Types, Dictionaries, and Methods samples; calling a method of a `HintPath`-referenced assembly and of a NuGet package (skipped when restore fails without network); handwritten code built with generated code; checked-in sample code matches the generator. |

## Conventions

- Graph fixtures are built with `GraphBuilder` (readable node IDs `n1`, `n2`, …).
- Tests that write files use `TemporaryDirectory` under the system temp folder.
- Integration tests copy samples into a temporary folder before generating, so the repository is never modified by tests.
- UI behavior is verified manually in the running app (open, edit, connect, mismatch rejection, wire-drop quick add, palette drag, copy/paste/duplicate, inline value editing, comments, reroute points, minimap, frame statistics, command palette, keyboard connecting, undo, run with input). Logic behind UI features lives in `GraphEditor` so it is covered by unit tests; there are no automated UI tests.
- Never automate the desktop (synthetic keyboard or mouse input) without the user's explicit go-ahead: input goes to whichever window has focus.
