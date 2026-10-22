# VisualStudioLikeIDE

A visual programming IDE for general-purpose C#/.NET 10 development. You connect nodes to describe program logic; a real compiler turns the graph into ordinary, readable C# inside a standard SDK-style project.

**Visual graph → compiler → clean C# source → `dotnet build` → standalone application.**

The generated program has no dependency on this IDE: no graph interpreter, no runtime library, no reflection. You can open it in Visual Studio, Rider, or VS Code and maintain it like any other C# project.

## Status

Phase 0 (technical design) and Phase 1 (a working end-to-end vertical slice) are complete. See [docs/ROADMAP.md](docs/ROADMAP.md) for what exists and what comes next.

What works today:

- Create or open a project, edit its entry graph on a node canvas (pan, zoom, box-select, move, connect, disconnect, delete, undo/redo).
- Built-in nodes: Entry, Branch, Return, console input/output, literals, arithmetic, comparisons, logic, string concatenation, conversions.
- Live type-checked diagnostics linked to nodes and pins, shown on the canvas and in the Problems panel.
- Readable C# generated through Roslyn syntax trees, with `if`/`else` structured from the execution graph, a node↔code source map, and Roslyn verification before anything is written.
- Build and run with the installed .NET SDK from the IDE, with program input and output in the Output panel.
- A headless CLI (`vside generate|build|run`) for scripts and CI.

## Example

The `samples/ConsoleApplication` graph generates:

```csharp
internal static class Program
{
    public static void Main()
    {
        Console.WriteLine("What is your name?");
        string line = Console.ReadLine() ?? "";
        if (line == "")
        {
            Console.WriteLine("You did not enter a name.");
            return;
        }

        Console.WriteLine("Hello, " + line + "!");
        Console.WriteLine("Goodbye.");
    }
}
```

## Getting started

Requires the .NET 10 SDK.

```sh
dotnet build VisualStudioLikeIDE.slnx
dotnet run --project src/VisualStudioLikeIDE.App                          # the IDE
dotnet run --project src/VisualStudioLikeIDE.App -- samples/HelloWorld/HelloWorld.vside
dotnet run --project src/VisualStudioLikeIDE.Cli -- run samples/ConsoleApplication/ConsoleApplication.vside
dotnet test VisualStudioLikeIDE.slnx
```

The samples also build without the IDE: `cd samples/HelloWorld && dotnet run`.

### Editor controls

| Action | Input |
| --- | --- |
| Add a node | Drag from the palette onto the graph, or search the palette and double-click or press Enter |
| Add a connected node | Drag a wire from a pin and release on empty space, then pick from the compatible nodes |
| Connect | Drag from a pin to a compatible pin (either direction) |
| Disconnect | Alt+click a pin |
| Move / select | Drag a node; Ctrl+click toggles; drag on empty space box-selects |
| Pan / zoom | Right- or middle-drag; mouse wheel |
| Align or distribute | Select items, then Edit → Arrange (align edges or centers; space three or more evenly) |
| Connect from the keyboard | Select two nodes, press Ctrl+L (Edit → Connect Pins…), pick a connection or a wire to remove, Enter |
| Loops | While Loop and For Loop (Flow category): Loop Body runs each iteration, Completed runs afterwards. Break and Continue work inside a body |
| Variables | Click empty canvas; in Details → Variables, Add Variable and set its name, type, and initial value. Its Get and Set nodes appear in the palette under Variables |
| Command palette | Ctrl+Shift+P: search every menu command and node by name; Enter runs the first match |
| Frame graph | Home |
| Frame time | View → Frame Statistics shows how long the canvas takes to draw |
| Minimap | Click or drag in the overview (lower right) to move the view; View → Minimap shows or hides it |
| Edit a value in place | Double-click the value under a node's title, or select the node and press F2 (Booleans toggle) |
| Comment / group | Press C (or Edit → Add Comment) to box the selection; drag its title to move it with the nodes inside, drag its corner to resize, double-click its title or press F2 to rename |
| Reroute a wire | Double-click a wire to add a point (keep holding to drag it); drag a point to move it; Alt+click it to remove it |
| Cut, copy, paste, duplicate | Ctrl+X, Ctrl+C, Ctrl+V (at the pointer), Ctrl+D |
| Delete, undo, redo, save | Delete, Ctrl+Z, Ctrl+Y, Ctrl+S |
| Generate, build, run, stop | F6, Ctrl+Shift+B, F5, Shift+F5 |

## Repository layout

```
src/
  VisualStudioLikeIDE.GraphModel     graph documents and the versioned JSON format
  VisualStudioLikeIDE.Compiler       validation, IR, control-flow analysis, Roslyn emission, node library
  VisualStudioLikeIDE.NodeEditor     undoable editing operations (no UI dependency)
  VisualStudioLikeIDE.ProjectSystem  project files, generated-code ownership, dotnet build/run
  VisualStudioLikeIDE.App            Avalonia desktop IDE
  VisualStudioLikeIDE.Cli            headless generate/build/run
tests/                               unit, integration (real dotnet builds), performance harness
samples/                             HelloWorld, ConsoleApplication, Variables, Loops, Functions, Exceptions, Collections
docs/                                architecture, compiler pipeline, formats, decisions, roadmap
```

## Security

Opening a project never builds or runs it; building and running always require an explicit command. Treat projects from others as untrusted code: building runs MSBuild and running executes the program with your permissions.

## Documentation

[Architecture](docs/ARCHITECTURE.md) · [Compiler pipeline](docs/COMPILER_PIPELINE.md) · [Code generation](docs/CODE_GENERATION.md) · [Graph format](docs/GRAPH_FORMAT.md) · [Node authoring](docs/NODE_AUTHORING.md) · [Decisions](docs/DECISIONS.md) · [Testing](docs/TESTING.md) · [Performance](docs/PERFORMANCE.md) · [Roadmap](docs/ROADMAP.md)
