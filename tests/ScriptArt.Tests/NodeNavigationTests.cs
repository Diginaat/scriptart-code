using ScriptArt.Compiler.Nodes;
using ScriptArt.GraphModel;
using ScriptArt.NodeEditor;

namespace ScriptArt.Tests;

public sealed class NodeNavigationTests
{
    private static readonly NodeLibrary Library = NodeLibrary.CreateDefault();

    [Fact]
    public void NodesLeadToTheGraphsAndVariablesThatDefineThem()
    {
        var program = new GraphBuilder();
        GraphBuilder greet = GraphBuilder.Function("Greet");
        var point = new GraphBuilder("type-point", "Point", GraphKind.Class);
        point.Graph.Properties.Add(new GraphProperty { Id = "x", Name = "X", Type = "int", Value = "0" });
        string variable = program.Variable("count", "int", "0");
        string call = Node(program, FunctionNodes.CallTypeId, (FunctionNodes.FunctionProperty, greet.Graph.Id));
        string create = Node(program, TypeNodes.CreateTypeId, (TypeNodes.TypeProperty, point.Graph.Id));
        string getX = Node(program, TypeNodes.GetTypeId, (TypeNodes.TypeProperty, point.Graph.Id), (TypeNodes.PropertyProperty, "x"));
        string get = program.VariableNode(variable, set: false);
        string print = program.Node("console.write-line");
        string missing = Node(program, FunctionNodes.CallTypeId, (FunctionNodes.FunctionProperty, "deleted"));
        var editor = new GraphEditor(program.Graph, Library) { ProjectGraphs = [program.Graph, greet.Graph, point.Graph] };

        Assert.Equal(new NodeNavigationTarget(greet.Graph.Id), editor.NavigationTargetOf(call));
        Assert.Equal(new NodeNavigationTarget(point.Graph.Id), editor.NavigationTargetOf(create));
        Assert.Equal(new NodeNavigationTarget(point.Graph.Id, PropertyId: "x"), editor.NavigationTargetOf(getX));
        Assert.Equal(new NodeNavigationTarget(program.Graph.Id, VariableId: variable), editor.NavigationTargetOf(get));
        Assert.Null(editor.NavigationTargetOf(print));
        Assert.Null(editor.NavigationTargetOf(missing));
        Assert.Null(editor.NavigationTargetOf("no-such-node"));
    }

    private static string Node(GraphBuilder builder, string type, params (string Name, string Value)[] properties)
    {
        string id = builder.Node(type);
        GraphNode node = builder.Graph.Nodes.Single(candidate => candidate.Id == id);
        foreach ((string name, string value) in properties)
        {
            node.Properties[name] = value;
        }

        return id;
    }
}
