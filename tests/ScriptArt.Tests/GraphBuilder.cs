using ScriptArt.GraphModel;

namespace ScriptArt.Tests;

/// <summary>Concise construction of graphs for tests, with readable deterministic node ids.</summary>
internal sealed class GraphBuilder
{
    private readonly GraphDocument graph;
    private int nextId = 1;

    public GraphBuilder(string id = "graph", string name = "Program", GraphKind kind = GraphKind.EntryPoint, string? returnType = null)
    {
        graph = new GraphDocument { Id = id, Name = name, Kind = kind, ReturnType = returnType };
    }

    /// <summary>A function graph with an id derived from its name; add its entry node with <see cref="Node"/>.</summary>
    public static GraphBuilder Function(string name, string? returnType = null) => new($"fn-{name}", name, GraphKind.Function, returnType);

    public GraphDocument Graph => graph;

    public string Node(string type, string? value = null)
    {
        var node = new GraphNode { Id = $"n{nextId++}", Type = type };
        if (value is not null)
        {
            node.Properties["value"] = value;
        }

        graph.Nodes.Add(node);
        return node.Id;
    }

    public GraphBuilder Connect(string fromNode, string fromPin, string toNode, string toPin)
    {
        graph.Connections.Add(new GraphConnection(new PinRef(fromNode, fromPin), new PinRef(toNode, toPin)));
        return this;
    }

    /// <summary>Connects execution: <paramref name="fromNode"/>.then to <paramref name="toNode"/>.exec.</summary>
    public GraphBuilder Then(string fromNode, string toNode, string fromPin = "then") => Connect(fromNode, fromPin, toNode, "exec");

    /// <summary>Declares a graph variable and returns its id.</summary>
    public string Variable(string name, string type, string value)
    {
        var variable = new GraphVariable { Id = $"v{nextId++}", Name = name, Type = type, Value = value };
        graph.Variables.Add(variable);
        return variable.Id;
    }

    /// <summary>Adds a Get (<paramref name="set"/> false) or Set node for a variable and returns its id.</summary>
    public string VariableNode(string variableId, bool set)
    {
        var node = new GraphNode { Id = $"n{nextId++}", Type = set ? "variable.set" : "variable.get" };
        node.Properties["variable"] = variableId;
        graph.Nodes.Add(node);
        return node.Id;
    }

    /// <summary>Declares a function parameter and returns its id, which is also its pin id.</summary>
    public string Parameter(string name, string type)
    {
        var parameter = new GraphParameter { Id = $"p{nextId++}", Name = name, Type = type };
        graph.Parameters.Add(parameter);
        return parameter.Id;
    }

    /// <summary>Adds a node that calls <paramref name="function"/> and returns its id.</summary>
    public string Call(GraphDocument function)
    {
        var node = new GraphNode { Id = $"n{nextId++}", Type = "function.call" };
        node.Properties["function"] = function.Id;
        graph.Nodes.Add(node);
        return node.Id;
    }

    /// <summary>A class or struct graph with an id derived from its name; add properties with <see cref="Property"/>.</summary>
    public static GraphBuilder Type(string name, GraphKind kind = GraphKind.Class) => new($"type-{name}", name, kind);

    /// <summary>Declares a property of a class or struct and returns its id.</summary>
    public string Property(string name, string type, string value = "")
    {
        var property = new GraphProperty { Id = $"p{nextId++}", Name = name, Type = type, Value = value };
        graph.Properties.Add(property);
        return property.Id;
    }

    /// <summary>Adds a type node (New, Get, Set, or With) for <paramref name="type"/> and, for property nodes, its property.</summary>
    public string TypeNode(string typeId, GraphDocument type, string? propertyId = null)
    {
        var node = new GraphNode { Id = $"n{nextId++}", Type = typeId };
        node.Properties["type"] = type.Id;
        if (propertyId is not null)
        {
            node.Properties["property"] = propertyId;
        }

        graph.Nodes.Add(node);
        return node.Id;
    }

    /// <summary>Adds a generated list or dictionary node (list.add, dictionary.set, …) for the collection type named <paramref name="collection"/>.</summary>
    public string CollectionNode(string typeId, string collection)
    {
        var node = new GraphNode { Id = $"n{nextId++}", Type = typeId };
        node.Properties["collection"] = collection;
        graph.Nodes.Add(node);
        return node.Id;
    }

    /// <summary>Sets a property of a node already added, such as a generic type's type arguments, and returns the node id.</summary>
    public string With(string nodeId, string property, string value)
    {
        graph.FindNode(nodeId)!.Properties[property] = value;
        return nodeId;
    }

    /// <summary>Adds a Print Line node fed by a string literal and returns the print node id.</summary>
    public string PrintLiteral(string text)
    {
        string print = Node("console.write-line");
        string literal = Node("literal.string", text);
        Connect(literal, "value", print, "text");
        return print;
    }
}
