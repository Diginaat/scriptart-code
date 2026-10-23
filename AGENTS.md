# Working in this repository

- Target: .NET 10, C# latest. `Directory.Build.props` sets nullable, warnings as errors, and recommended analyzers. Fix warnings; do not suppress them without a written justification.
- Dependency direction: GraphModel ← NodeSdk ← Compiler ← NodeEditor / ProjectSystem ← App / Cli. `ScriptArt.NodeSdk` is the public API of node libraries (published as a package): keep it free of Roslyn and of compiler internals, and treat changes to its public types as breaking for third-party libraries. Only `ScriptArt.App` may reference Avalonia. The compiler and graph model must stay UI-free.
- Never generate C# by string concatenation. Lower to IR (`Compiler/Ir`), then emit with Roslyn syntax factories (`Compiler/Emit/CSharpEmitter.cs`).
- Generated files must start with the ownership header (`GeneratedSource.OwnershipMarker`). Never overwrite or delete a file without it.
- The compiler must never emit code for a graph that has errors.
- Node type IDs (`core.entry`, `console.write-line`, …) and pin IDs are part of the file format. Do not rename them; add new nodes instead.
- Graph or project schema changes: bump `CurrentSchemaVersion`, add a migration step in the serializer, and keep the existing version-1 fixture test passing.
- After changing code generation, regenerate the samples (`dotnet run --project src/ScriptArt.Cli -- generate samples/<name>/<name>.vside`); `CheckedInSampleCodeMatchesGenerator` fails otherwise.
- Never load a node library (`NodeLibraryLoader`) that the user did not approve (`NodeLibraryTrust`) or name on the command line; opening a project must never run code.
- Tests: `dotnet test tests/ScriptArt.Tests` is fast (no processes). `tests/ScriptArt.IntegrationTests` builds and runs real projects (~10 s). Run both before finishing.
- Keep docs in `docs/` current with the code, especially ROADMAP.md and DECISIONS.md.
