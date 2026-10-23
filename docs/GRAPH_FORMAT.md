# Graph and project file formats

Both formats are UTF-8 JSON without a BOM, indented with two spaces, with `\n` line endings. Property maps are sorted by key, so saving an unchanged document produces identical bytes. Writes are atomic.

## Graph (`*.graph.json`, schema version 8)

```json
{
  "schemaVersion": 8,
  "id": "hello-world-program",
  "name": "Program",
  "kind": "EntryPoint",
  "nodes": [
    { "id": "entry", "type": "core.entry", "x": 0, "y": 0, "properties": {} },
    { "id": "print", "type": "console.write-line", "x": 240, "y": 0, "properties": {} },
    { "id": "greeting", "type": "literal.string", "x": 0, "y": 120, "properties": { "value": "Hello, World!" } }
  ],
  "connections": [
    { "from": { "node": "entry", "pin": "then" }, "to": { "node": "print", "pin": "exec" } },
    { "from": { "node": "greeting", "pin": "value" }, "to": { "node": "print", "pin": "text" }, "points": [ { "x": 120, "y": 60 } ] }
  ],
  "comments": [
    { "id": "c1", "x": -24, "y": -42, "width": 520, "height": 220, "text": "Greet the user" }
  ],
  "variables": [
    { "id": "v1", "name": "count", "type": "int", "value": "0" }
  ],
  "parameters": [],
  "properties": []
}
```

| Field | Meaning |
| --- | --- |
| `schemaVersion` | Format version. Required. |
| `id` | Stable graph identifier, used in diagnostics and source maps. |
| `name` | Generated class name; must be a C# identifier. |
| `kind` | `EntryPoint` (generates `Main`), `Function` (generates a public static method named `name`, or a public instance method for a method of a class or struct), `Constructor` (generates a constructor of its `owner`; added in version 8), `Class` (generates an `internal sealed class`), or `Struct` (generates an `internal struct`). A class or struct graph has properties and no nodes. |
| `nodes[].id` | Stable node identifier, unique within the graph. New nodes use a GUID without dashes. |
| `nodes[].type` | Node definition ID. |
| `nodes[].x`, `y` | Position in canvas units. Node size is derived, not stored. |
| `nodes[].properties` | Editable values as strings, parsed with the invariant culture. |
| `connections[]` | Directed from an output pin to an input pin. A connection is identified by its two pins. |
| `connections[].points` | Optional reroute points the wire passes through, in order from the output (added in version 3). Layout only; omitted for direct wires. |
| `comments[]` | Titled boxes drawn behind nodes (added in version 2). `x`, `y`, `width`, `height` in canvas units; `text` is the title. Comments never affect generated code. Membership is not stored: a node belongs to a comment while it lies completely inside it, so moving nodes in or out needs no bookkeeping. |
| `variables[]` | Local variables of the graph's method (added in version 4). `name` is the C# local name; `type` is `bool`, `int`, `double`, `string`, the `name` of a class or struct graph of the project, a list of one of these (`List<int>`, `List<Point>`), or a dictionary from `int` or `string` to one of these (`Dictionary<string, int>`, written with `, ` between the type arguments); `value` is the initial value as invariant-culture text, and must be blank for a list or dictionary, which starts empty, and for a class or struct, which starts as a new object. |
| `parameters[]` | Parameters of a `Function` graph, in order (added in version 5). `id` is stable and doubles as the pin id; `name` is the C# parameter name; `type` is any variable type, including list types. |
| `returnType` | C# name of a function's return type, any variable type. Omitted for a function that returns nothing. |
| `owner` | For a `Function` graph that is a method of a class or struct, and for a `Constructor` graph, that type graph's `id` (added in version 7). A constructor's `name` only names its files; the generated constructor has the type's name. Omitted for a function of the program class. A method's file is `Graphs/{type}.{name}.graph.json` and its generated file `{type}.{name}.g.cs`. |
| `properties[]` | Properties of a `Class` or `Struct` graph, in order (added in version 6). `id` is stable and is what property nodes refer to; `name` is the C# property name; `type` and `value` work like a variable's. |
| `typeParameters[]` | Type parameter names of a generic `Class` or `Struct` graph, such as `T` (added in version 8). Inside the type and its methods and constructors they can be used as types; elsewhere the type is written with arguments, such as `Box<string>`. |

Variable nodes use the types `variable.get` and `variable.set`, with the property `variable` holding the variable's `id`. Their title and pin types come from the variable, so renaming or retyping a variable updates every node that uses it.

Function graphs use `function.entry` (outputs: one per parameter) and `function.return` (input `value`). A call node has the type `function.call` and the property `function` holding the called graph's `id`; its pins follow the function's current signature, so renaming a function or a parameter keeps every wire.

Nodes of lists of classes and structs (`list.create`, `list.add`, `list.set`, `list.remove-at`, `list.clear`, `list.get`, `list.count`, `list.contains`, `flow.foreach`) and of dictionaries (`dictionary.create`, `dictionary.set`, `dictionary.remove`, `dictionary.clear`, `dictionary.get`, `dictionary.contains-key`, `dictionary.count`, `dictionary.keys`, `dictionary.values`) have the property `collection` holding the C# name of the collection type, for example `List<Point>`. Lists of `bool`, `int`, `double`, and `string` use the per-type nodes such as `list.int.add`.

A method's call node (`function.call`) has a `target` input for the object or value whose method runs. The `type.self` node, allowed only in a method graph, outputs that object or value and has no properties.

Type nodes have the types `type.create`, `type.get-property`, `type.set-property` (classes), and `type.with-property` (structs), with the property `type` holding the class or struct graph's `id` and, except for `type.create`, `property` holding the property's `id`. A `type.create` node that runs a constructor has the property `constructor` holding the constructor graph's `id`; without it, the node generates `new T()`. Type nodes and call nodes of a generic type have the property `typeArguments`, the comma-separated C# names of its type arguments, such as `string`.

Variable, parameter, and property types nest: `List<List<int>>`, `Dictionary<string, List<Point>>`. A framework type that graphs can hold, such as `System.Text.StringBuilder`, is written with its namespace.

`flow.try` has the property `exception`, the C# name of the exception type its Catch output handles (default `Exception`), and `flow.throw` the property `exception`, the type it throws (default `InvalidOperationException`). `flow.rethrow` has no properties and is allowed only in a Catch block.

.NET member nodes have the type `dotnet:` plus the member's documentation ID: `dotnet:M:…` for methods and constructors (`#ctor`), `dotnet:P:…` to read a property, and `dotnet:set:P:…` to set one. A generic method node has the property `typeArguments`.

Absent lists (`nodes`, `connections`, `comments`, `variables`, `parameters`, `properties`, `typeParameters`) read as empty.

Pins are not stored; they are reconstructed from node definitions. A file referring to an unknown node type still loads, and the compiler reports VSG0001, so missing libraries never destroy a graph. The editor draws a node it cannot resolve (an unknown type, a missing variable or function, or a generic type without its type arguments) as an **Unresolved** node with the pins its wires use, so it can be found and deleted.

## Project (`*.vside`, schema version 1)

```json
{
  "schemaVersion": 1,
  "name": "HelloWorld",
  "rootNamespace": "HelloWorld",
  "graphs": [ "Graphs/Program.graph.json" ]
}
```

Graph paths are relative, use `/`, and must stay inside the project directory. A project has one `EntryPoint` graph and any number of `Function`, `Class`, and `Struct` graphs; every graph can call every function and use every class and struct. Graph names are unique, because each generates `{name}.g.cs`. Build settings are in the user-owned `.csproj`.

## Clipboard fragments

Copied nodes and comments are placed on the system clipboard as text in the graph format above, with `id` set to `vside-clipboard-fragment`. Only connections between copied nodes are included. Paste accepts only text that parses as a graph with that ID, assigns new node IDs, and drops connections that do not join two pasted nodes; anything else invalid is reported by the compiler as usual.

## Versioning and migration

- A file with a newer `schemaVersion` than the IDE supports is rejected with a clear message; it is never partially loaded or overwritten.
- When the schema changes, `CurrentSchemaVersion` is incremented and a migration step that transforms the JSON object from version N to N+1 is added to the serializer. Steps run in order before deserialization.
- Version 1 → 2: adds an empty `comments` array.
- Version 2 → 3: nothing to convert; `points` is optional.
- Version 3 → 4: adds an empty `variables` array.
- Version 4 → 5: adds an empty `parameters` array.
- Version 5 → 6: adds an empty `properties` array.
- Version 6 → 7: nothing to convert; an absent `owner` means a function of the program class.
- Version 7 → 8: adds an empty `typeParameters`. Constructor graphs are new files, so nothing else changes.
- Older files (including the checked-in samples, which use versions 1 to 7) open unchanged and are written in the current version on the next save. The IDE notes such files in Output when a project opens.
- `vside migrate --check <project>` lists outdated files and the steps that would convert them (exit code 1 when there are any); `vside migrate <project>` converts every outdated file, after copying the originals to `MigrationBackup/<timestamp>/`. If any file cannot be read, nothing is written.
- Steps are listed in `GraphSerializer.Migrations`; `MigrationTests` checks that every version has exactly one, in order.
- `SerializationTests.Version1FileFromDiskLoads` keeps a literal version-1 file; it must keep passing for as long as version 1 is supported.
- Corrupted files produce `GraphFormatException` with the file path and reason. The previous file on disk is never touched by a failed load.
