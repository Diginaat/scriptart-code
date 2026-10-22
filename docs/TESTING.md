# Testing

```sh
dotnet test tests/VisualStudioLikeIDE.Tests              # fast: no processes, ~2 s
dotnet test tests/VisualStudioLikeIDE.IntegrationTests   # real dotnet build/run, ~10 s
dotnet run -c Release --project tests/VisualStudioLikeIDE.PerformanceTests
```

## Suites

| Suite | Covers |
| --- | --- |
| `CodeGenerationTests` | Exact generated text for representative graphs (hello world, rejoining branch, early return, inverted condition, precedence, literals, escaping, conversions, unique locals, determinism, source map, 5,000-node chain). Every generated file is also compiled with Roslyn and must have no diagnostics. |
| `DiagnosticTests` | Each validation rule: missing/multiple entry, unconnected input, type mismatch, kind mismatch, cycles, unavailable values, invalid literals, unknown types, unreachable/unused warnings, invalid names. |
| `SerializationTests` | Round-trips, determinism, LF endings, the literal version-1 fixture and its migration to the current version, comments, reroute points, malformed and future-version files, atomic writes. |
| `ProjectSystemTests` | Project creation and loading, refusal to overwrite, ownership conflicts, stale generated file removal, write-if-changed, Roslyn errors in handwritten code, path traversal. |
| `GraphEditorTests` | Undo/redo of every operation, connection rules and replacement, compatible-pin lookup for context-sensitive creation, add-and-connect as one step, copy fragments and paste (new IDs, placement, dangling connections), comments (add, rename, resize, move with contents, delete, copy/paste), reroute points (add, move, remove, identity, paste offset), align and distribute (`LayoutToolsTests`), command palette search ranking (`SearchMatcherTests`), possible connections between two nodes and single-wire disconnect, variables (generated code, diagnostics, per-node pins, editor operations and undo, schema migration) in `VariableTests`, loops (while, for, nesting, break/continue, scoping and flow errors) in `LoopTests`, dirty tracking. |
| `EndToEndTests` (integration) | New project builds and prints; the ConsoleApplication sample with and without input; the Variables, Loops, Functions, Exceptions, and Collections samples; calling a method of a `HintPath`-referenced assembly and of a NuGet package (skipped when restore fails without network); handwritten code built with generated code; checked-in sample code matches the generator. |

## Conventions

- Graph fixtures are built with `GraphBuilder` (readable node IDs `n1`, `n2`, …).
- Tests that write files use `TemporaryDirectory` under the system temp folder.
- Integration tests copy samples into a temporary folder before generating, so the repository is never modified by tests.
- UI behavior is verified manually in the running app (open, edit, connect, mismatch rejection, wire-drop quick add, palette drag, copy/paste/duplicate, inline value editing, comments, reroute points, minimap, frame statistics, command palette, keyboard connecting, undo, run with input). Logic behind UI features lives in `GraphEditor` so it is covered by unit tests; there are no automated UI tests.
- Never automate the desktop (synthetic keyboard or mouse input) without the user's explicit go-ahead: input goes to whichever window has focus.
