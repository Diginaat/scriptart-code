# Graph and project file formats

Both formats are UTF-8 JSON without a BOM, indented with two spaces, with `\n` line endings. Property maps are sorted by key, so saving an unchanged document produces identical bytes. Writes are atomic.

## Graph (`*.graph.json`, schema version 5)

```json
{
  "schemaVersion": 5,
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
  "parameters": []
}
```

| Field | Meaning |
| --- | --- |
| `schemaVersion` | Format version. Required. |
| `id` | Stable graph identifier, used in diagnostics and source maps. |
| `name` | Generated class name; must be a C# identifier. |
| `kind` | `EntryPoint` (generates `Main`) or `Function` (generates a private static method named `name`). |
| `nodes[].id` | Stable node identifier, unique within the graph. New nodes use a GUID without dashes. |
| `nodes[].type` | Node definition ID. |
| `nodes[].x`, `y` | Position in canvas units. Node size is derived, not stored. |
| `nodes[].properties` | Editable values as strings, parsed with the invariant culture. |
| `connections[]` | Directed from an output pin to an input pin. A connection is identified by its two pins. |
| `connections[].points` | Optional reroute points the wire passes through, in order from the output (added in version 3). Layout only; omitted for direct wires. |
| `comments[]` | Titled boxes drawn behind nodes (added in version 2). `x`, `y`, `width`, `height` in canvas units; `text` is the title. Comments never affect generated code. Membership is not stored: a node belongs to a comment while it lies completely inside it, so moving nodes in or out needs no bookkeeping. |
| `variables[]` | Local variables of the graph's method (added in version 4). `name` is the C# local name; `type` is `bool`, `int`, `double`, or `string`; `value` is the initial value as invariant-culture text. |
| `parameters[]` | Parameters of a `Function` graph, in order (added in version 5). `id` is stable and doubles as the pin id; `name` is the C# parameter name; `type` is `bool`, `int`, `double`, or `string`. |
| `returnType` | C# keyword of a function's return type. Omitted for a function that returns nothing. |

Variable nodes use the types `variable.get` and `variable.set`, with the property `variable` holding the variable's `id`. Their title and pin types come from the variable, so renaming or retyping a variable updates every node that uses it.

Function graphs use `function.entry` (outputs: one per parameter) and `function.return` (input `value`). A call node has the type `function.call` and the property `function` holding the called graph's `id`; its pins follow the function's current signature, so renaming a function or a parameter keeps every wire.

Absent lists (`nodes`, `connections`, `comments`, `variables`, `parameters`) read as empty.

Pins are not stored; they are reconstructed from node definitions. A file referring to an unknown node type still loads, and the compiler reports VSG0001, so missing libraries never destroy a graph.

## Project (`*.vside`, schema version 1)

```json
{
  "schemaVersion": 1,
  "name": "HelloWorld",
  "rootNamespace": "HelloWorld",
  "graphs": [ "Graphs/Program.graph.json" ]
}
```

Graph paths are relative, use `/`, and must stay inside the project directory. A project has one `EntryPoint` graph and any number of `Function` graphs; every graph can call every function. Build settings are in the user-owned `.csproj`.

## Clipboard fragments

Copied nodes and comments are placed on the system clipboard as text in the graph format above, with `id` set to `vside-clipboard-fragment`. Only connections between copied nodes are included. Paste accepts only text that parses as a graph with that ID, assigns new node IDs, and drops connections that do not join two pasted nodes; anything else invalid is reported by the compiler as usual.

## Versioning and migration

- A file with a newer `schemaVersion` than the IDE supports is rejected with a clear message; it is never partially loaded or overwritten.
- When the schema changes, `CurrentSchemaVersion` is incremented and a migration step that transforms the JSON object from version N to N+1 is added to the serializer. Steps run in order before deserialization.
- Version 1 → 2: adds an empty `comments` array.
- Version 2 → 3: nothing to convert; `points` is optional.
- Version 3 → 4: adds an empty `variables` array.
- Version 4 → 5: adds an empty `parameters` array.
- Older files (including the checked-in samples, which are version 1) open unchanged and are written in the current version on the next save.
- `SerializationTests.Version1FileFromDiskLoads` keeps a literal version-1 file; it must keep passing for as long as version 1 is supported.
- Corrupted files produce `GraphFormatException` with the file path and reason. The previous file on disk is never touched by a failed load.
