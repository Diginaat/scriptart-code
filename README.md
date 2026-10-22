# ScriptArt Code

A visual programming IDE for general-purpose C#/.NET 10 development. You connect nodes to describe program logic; a real compiler turns the graph into ordinary, readable C# inside a standard SDK-style project.

**Visual graph → compiler → clean C# source → `dotnet build` → standalone application.**

The generated program has no dependency on this IDE: no graph interpreter, no runtime library, no reflection. You can open it in Visual Studio, Rider, or VS Code and maintain it like any other C# project.

## Status

Phases 0 to 4 are complete: the visual editor, real programming capabilities (variables, loops, functions, collections, classes and structs, async, .NET APIs), and IDE integration (handwritten files, generation preview, Go to Definition, node debugging, solutions). See [docs/ROADMAP.md](docs/ROADMAP.md) for what exists and what comes next.

What works today:

- Create or open a project, edit its entry graph on a node canvas (pan, zoom, box-select, move, connect, disconnect, delete, undo/redo).
- Built-in nodes: Entry, Branch, Return, console input/output, literals, arithmetic, comparisons, logic, string concatenation, conversions.
- Live type-checked diagnostics linked to nodes and pins, shown on the canvas and in the Problems panel.
- Readable C# generated through Roslyn syntax trees, with `if`/`else` structured from the execution graph, a node↔code source map, and Roslyn verification before anything is written.
- Build and run with the installed .NET SDK from the IDE, with program input and output in the Output panel.
- Handwritten `.cs` files edited in the IDE and checked as you type; a diff preview before generated files are written; Go to Definition (F12).
- Debugging with breakpoints on nodes (F9, F5, F10, F11) through netcoredbg, with call stack and locals.
- Solutions, NuGet packages, and project references through the `dotnet` CLI.
- A headless CLI (`vside generate|diff|build|run`) for scripts and CI.

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
dotnet build ScriptArtCode.slnx
dotnet run --project src/ScriptArt.App                          # the IDE
dotnet run --project src/ScriptArt.App -- samples/HelloWorld/HelloWorld.vside
dotnet run --project src/ScriptArt.Cli -- run samples/ConsoleApplication/ConsoleApplication.vside
dotnet test ScriptArtCode.slnx
```

The samples also build without the IDE: `cd samples/HelloWorld && dotnet run`.

### Editor controls

| Action | Input |
| --- | --- |
| Add a node | Right-click empty space on the graph and search the action menu, or drag from the Palette tab onto the graph |
| Context sensitivity | The action menu's **Context Sensitive** box (on by default) shows only nodes that fit: from a released wire or a selected node, nodes that can connect to it, wired up when added. Clear it to see every node |
| Add a connected node | Drag a wire from a pin and release on empty space, then pick from the compatible nodes |
| Connect | Drag from a pin to a compatible pin (either direction) |
| Disconnect | Alt+click a pin |
| Move / select | Drag a node; Ctrl+click toggles; drag on empty space box-selects |
| Pan / zoom | Right- or middle-drag; mouse wheel |
| Align or distribute | Select items, then Edit → Arrange (align edges or centers; space three or more evenly) |
| Connect from the keyboard | Select two nodes, press Ctrl+L (Edit → Connect Pins…), pick a connection or a wire to remove, Enter |
| Loops | While Loop and For Loop (Flow category): Loop Body runs each iteration, Completed runs afterwards. Break and Continue work inside a body |
| My Project panel | Lists the graphs, functions, classes, structs, and the open graph's variables in collapsible sections, each with a **+** button; **+ Add New** offers all of them. Click a graph to open it |
| Variables | **+** next to Variables; select a variable to edit its name, type, and initial value in Details. Drag it onto the graph for a Get (hold Ctrl) or Set (hold Alt) node, or pick one from the menu |
| Classes and structs | **+** next to Classes or Structs; add properties in Details. New, Get, and Set (class) or With (struct) nodes appear under Types |
| Methods | **Add Method** in a class's or struct's Details; use its **Self** node inside the method and its Call node, with a Target, elsewhere |
| Lists and dictionaries | Choose a type such as `List<Point>` or `Dictionary<string, int>` for a variable, parameter, or property; its nodes appear under Lists or Dictionaries |
| Constructors and generic types | **Add Constructor** in a class's or struct's Details, then choose it on a New node; **Type parameters** (such as `T`) make the type generic |
| Command palette | Ctrl+Shift+P: search every menu command and node by name; Enter runs the first match |
| Frame graph | Home |
| Frame time | View → Frame Statistics shows how long the canvas takes to draw |
| Minimap | Click or drag in the overview (lower right) to move the view; View → Minimap shows or hides it |
| Edit a value in place | Double-click the value under a node's title, or select the node and press F2 (Booleans toggle) |
| Comment / group | Press C (or Edit → Add Comment) to box the selection; drag its title to move it with the nodes inside, drag its corner to resize, double-click its title or press F2 to rename |
| Reroute a wire | Double-click a wire to add a point (keep holding to drag it); drag a point to move it; Alt+click it to remove it |
| Cut, copy, paste, duplicate | Ctrl+X, Ctrl+C, Ctrl+V (at the pointer), Ctrl+D |
| Delete, undo, redo, save | Delete, Ctrl+Z, Ctrl+Y, Ctrl+S |
| Handwritten C# | My Project → Files: click a file to edit it in the Code tab; Project → Add C# File. Saved with Ctrl+S |
| Go to definition | F12 on a selected node or on a name in the code |
| Generate (with preview), build | F6, Ctrl+Shift+B. Build → Preview Changes Before Generating turns the diff preview on or off |
| Debug, run, stop | F5 (debug), Ctrl+F5 (run without debugging), Shift+F5 |
| Breakpoints and stepping | F9 on a node; while paused F5 continues, F10 steps over, F11 steps into, Shift+F11 steps out. Needs [netcoredbg](https://github.com/Samsung/netcoredbg/releases) on PATH or in `VSIDE_NETCOREDBG` |
| Packages, references, solution | Project menu, or **+** next to References in My Project |

## Repository layout

```
src/
  ScriptArt.GraphModel     graph documents and the versioned JSON format
  ScriptArt.Compiler       validation, IR, control-flow analysis, Roslyn emission, node library
  ScriptArt.NodeEditor     undoable editing operations (no UI dependency)
  ScriptArt.ProjectSystem  project files, generated-code ownership, dotnet build/run
  ScriptArt.App            Avalonia desktop IDE
  ScriptArt.Cli            headless generate/build/run
tests/                               unit, integration (real dotnet builds), performance harness
samples/                             HelloWorld, ConsoleApplication, Variables, Loops, Functions, Exceptions, Collections, Types, Dictionaries, Methods
docs/                                architecture, compiler pipeline, formats, decisions, roadmap
```

## Security

Opening a project never builds or runs it; building and running always require an explicit command. Treat projects from others as untrusted code: building runs MSBuild and running executes the program with your permissions.

## Documentation

[Architecture](docs/ARCHITECTURE.md) · [Compiler pipeline](docs/COMPILER_PIPELINE.md) · [Code generation](docs/CODE_GENERATION.md) · [Graph format](docs/GRAPH_FORMAT.md) · [Node authoring](docs/NODE_AUTHORING.md) · [Decisions](docs/DECISIONS.md) · [Testing](docs/TESTING.md) · [Performance](docs/PERFORMANCE.md) · [Roadmap](docs/ROADMAP.md)
