namespace VisualStudioLikeIDE.GraphModel;

/// <summary>
/// The kind of code a graph compiles to.
/// </summary>
public enum GraphKind
{
    /// <summary>The application entry point (<c>Program.Main</c>).</summary>
    EntryPoint,

    /// <summary>A method of the program class, with <see cref="GraphDocument.Parameters"/> and an optional return value.</summary>
    Function,
}

/// <summary>
/// A persistent visual graph: nodes, connections, and layout. Contains no transient UI state.
/// </summary>
public sealed class GraphDocument
{
    /// <summary>
    /// Version 2 added <see cref="Comments"/>; version 3 added <see cref="GraphConnection.Points"/>; version 4 added <see cref="Variables"/>;
    /// version 5 added <see cref="Parameters"/> and <see cref="ReturnType"/>.
    /// </summary>
    public const int CurrentSchemaVersion = 5;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Stable identifier used to map generated code back to this graph.</summary>
    public required string Id { get; init; }

    /// <summary>Name of the generated type. Must be a valid C# identifier.</summary>
    public required string Name { get; set; }

    public GraphKind Kind { get; set; } = GraphKind.EntryPoint;

    public List<GraphNode> Nodes { get; init; } = [];

    public List<GraphConnection> Connections { get; init; } = [];

    /// <summary>Titled boxes drawn behind nodes. They group nodes for the reader and never affect generated code.</summary>
    public List<GraphComment> Comments { get; init; } = [];

    /// <summary>Local variables of the graph's method, read and written by Get and Set nodes.</summary>
    public List<GraphVariable> Variables { get; init; } = [];

    /// <summary>Parameters of a <see cref="GraphKind.Function"/> graph, in declaration order.</summary>
    public List<GraphParameter> Parameters { get; init; } = [];

    /// <summary>C# keyword of a function's return type, or null when it returns nothing (<c>void</c>).</summary>
    public string? ReturnType { get; set; }

    public GraphNode? FindNode(string nodeId) => Nodes.Find(node => node.Id == nodeId);

    public GraphVariable? FindVariable(string variableId) => Variables.Find(variable => variable.Id == variableId);

    public GraphParameter? FindParameter(string parameterId) => Parameters.Find(parameter => parameter.Id == parameterId);

    public GraphComment? FindComment(string commentId) => Comments.Find(comment => comment.Id == commentId);

    public static GraphDocument CreateEmpty(string name, GraphKind kind) => new()
    {
        Id = NewId(),
        Name = name,
        Kind = kind,
    };

    /// <summary>Creates a new stable identifier for graphs and nodes.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");
}

/// <summary>
/// An instance of a node definition placed in a graph. Pins are reconstructed from the definition named by <see cref="Type"/>.
/// </summary>
public sealed class GraphNode
{
    public required string Id { get; init; }

    /// <summary>Identifier of the node definition, for example <c>console.write-line</c>.</summary>
    public required string Type { get; init; }

    public double X { get; set; }

    public double Y { get; set; }

    /// <summary>Editable node values such as literal text. Sorted so serialization is deterministic.</summary>
    public SortedDictionary<string, string> Properties { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>
/// A titled box on the canvas. Moving it moves the nodes inside it, so it also serves as a group.
/// </summary>
public sealed class GraphComment
{
    public required string Id { get; init; }

    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public string Text { get; set; } = "";
}

/// <summary>
/// A local variable. Nodes refer to it by <see cref="Id"/>, so renaming it keeps every Get and Set node attached.
/// </summary>
public sealed class GraphVariable
{
    public required string Id { get; init; }

    /// <summary>Name of the generated local. Must be a valid C# identifier, unique in the graph.</summary>
    public required string Name { get; set; }

    /// <summary>C# keyword of the variable's type: <c>bool</c>, <c>int</c>, <c>double</c>, or <c>string</c>.</summary>
    public required string Type { get; set; }

    /// <summary>Initial value, as literal text in the invariant culture.</summary>
    public string Value { get; set; } = "";
}

/// <summary>
/// A function parameter. Its id is also the pin id on the function's entry node and on every call node, so renaming it keeps wires attached.
/// </summary>
public sealed class GraphParameter
{
    public required string Id { get; init; }

    /// <summary>Name of the generated parameter. Must be a valid C# identifier, unique in the function.</summary>
    public required string Name { get; set; }

    /// <summary>C# keyword of the parameter's type: <c>bool</c>, <c>int</c>, <c>double</c>, or <c>string</c>.</summary>
    public required string Type { get; set; }
}

/// <summary>Identifies one pin on one node.</summary>
public sealed record PinRef(string Node, string Pin);

/// <summary>
/// A directed link from an output pin to an input pin. Execution and data links share this shape;
/// the pin definitions decide which kind a connection is.
/// </summary>
/// <remarks>A connection is identified by its two pins: equality ignores <see cref="Points"/>, which is layout only.</remarks>
public sealed record GraphConnection(PinRef From, PinRef To)
{
    /// <summary>Reroute points the wire passes through, in order from <see cref="From"/> to <see cref="To"/>; null for a direct wire.</summary>
    public List<GraphPoint>? Points { get; set; }

    public bool Equals(GraphConnection? other) => other is not null && From == other.From && To == other.To;

    public override int GetHashCode() => HashCode.Combine(From, To);
}

/// <summary>A position in canvas units.</summary>
public sealed record GraphPoint(double X, double Y);
