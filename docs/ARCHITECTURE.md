# Architecture

## Principle

The editor produces source code, not a program. Everything the user builds becomes ordinary C# in a standard SDK-style project. The IDE's own runtime machinery (canvas, editing model, compiler) never ships with the user's application.

## Projects and dependencies

```
GraphModel ──► NodeSdk ──► Compiler ──► NodeEditor ──┐
                                │                     ├──► App (Avalonia)
                                └──► ProjectSystem ───┤
                                                      └──► Cli
```

| Project | Responsibility | Depends on |
| --- | --- | --- |
| GraphModel | `GraphDocument`, nodes, connections, positions; versioned JSON serialization; atomic file writes | BCL only |
| NodeSdk | The public API of node libraries: `NodeDefinition`, pins, `TypeRef`, the IR, lowering contexts, `INodeProvider`. Published as a package | GraphModel |
| Compiler | Built-in node library and loading of external ones, validation, control-flow analysis, simplification, incremental compilation, Roslyn emission and verification | NodeSdk, Microsoft.CodeAnalysis.CSharp |
| NodeEditor | `GraphEditor`: every graph edit as an undoable operation, live connection checks; `SpatialIndex` for the canvas | Compiler |
| ProjectSystem | `.vside` project files, schema migration, approved node libraries, project layout, generated-file ownership and change plans, handwritten files, `dotnet build`/`run`, solutions and references through the `dotnet` CLI, debugging over the Debug Adapter Protocol (`Debugging/`) | Compiler |
| App | Desktop IDE: canvas, My Project, palette, Files and editor tabs, inspector, problems, code view and handwritten-file editor, generation preview, debugger, output | NodeEditor, ProjectSystem, Avalonia, AvaloniaEdit |
| Cli | `vside generate|diff|build|run|migrate` | ProjectSystem |

Several subsystems suggested in the original plan (TypeSystem, Diagnostics, CodeGeneration, NodeLibrary, Serialization, BuildSystem) are folders inside the projects above rather than separate assemblies. They become separate projects when they gain a consumer that needs them independently, as the node-library SDK did (see [DECISIONS.md](DECISIONS.md), D-004 and D-038).

## Data flow

1. **Persisted state** — `GraphDocument` (GraphModel). Nodes store a type ID, position, and string properties; pins are not persisted but reconstructed from the node definition.
2. **Editing** — the view calls `GraphEditor`, which mutates the document, records undo steps, and raises `Changed`. Transient state (selection, hover, drag previews, layout, zoom) lives only in the canvas.
3. **Compilation** — on change (debounced 250 ms) the App takes a read-only snapshot of the project, compiles the open graph on a background thread, and shows diagnostics and the generated code; a result superseded by a newer edit is dropped (D-041). Nothing is written. Project-wide compiles reuse the results of unchanged graphs (D-039).
4. **Generation** — F6 / Build: `VisualProject.PreviewGeneration` compiles all graphs, verifies generated plus handwritten C# (with unsaved editor text) with Roslyn, and plans the file changes; the IDE can show the plan as diffs. `VisualProject.Generate` then writes `Generated/*.g.cs`. Nothing is written when verification fails.
5. **Build/run** — `DotNetCli` runs `dotnet build` and `dotnet run --no-build` with argument lists (no shell). Output streams to the Output panel; stdin is forwarded from the input box.
6. **Debug** — `DebugSession` starts the built program with a startup hook that waits for a debugger, attaches netcoredbg over DAP (`DebugAdapterConnection`), and maps node breakpoints and stops through `DebugSourceMap` (the generated source map).

## Project layout on disk

```
MyApp/
  MyApp.vside            project file (name, root namespace, graph list)
  MyApp.csproj           created once; owned by the user afterwards
  Graphs/Program.graph.json
  Generated/Program.g.cs owned by the generator (header-marked)
  MyApp.slnx             optional solution, created on request
  *.cs                   handwritten code, edited in the IDE's editor tabs and saved only on request
```

The project file deliberately does not end in `proj`; MSBuild treats `*.*proj` files as projects, which would make a bare `dotnet build` in the folder ambiguous.

## Extensibility seams

- `NodeDefinition` subclasses (`PureNodeDefinition`, `StatementNodeDefinition`) lower to IR through `ExpressionLoweringContext` / `StatementLoweringContext`. They never touch Roslyn or the UI. See [NODE_AUTHORING.md](NODE_AUTHORING.md).
- `NodeLibrary` is the registry; the compiler and editor take a library instance, so additional libraries can be registered without changing either.
- IR is the contract between lowering and emission; new language features add IR nodes and emitter cases.
