using VisualStudioLikeIDE.Compiler.Ir;
using VisualStudioLikeIDE.Compiler.Lowering;
using VisualStudioLikeIDE.Compiler.TypeSystem;

namespace VisualStudioLikeIDE.Compiler.Nodes;

/// <summary>
/// Nodes for <c>List&lt;T&gt;</c>. Pins are typed, and the type system has no generic pins, so each node is registered once per
/// element type, like the arithmetic nodes: <c>list.int.add</c>, <c>list.string.add</c>, and so on.
/// </summary>
public static class CollectionNodes
{
    public const string Category = "Lists";

    private static readonly (TypeRef Type, string Suffix, string Label)[] Elements =
    [
        (TypeRef.Boolean, "bool", "Boolean"),
        (TypeRef.Int32, "int", "Integer"),
        (TypeRef.Double, "double", "Number"),
        (TypeRef.String, "string", "String"),
    ];

    public static IEnumerable<NodeDefinition> All()
    {
        foreach ((TypeRef element, string suffix, string label) in Elements)
        {
            TypeRef list = TypeRef.ListOf(element);
            PinDefinition listIn = PinDefinition.DataIn("list", "List", list);
            PinDefinition index = PinDefinition.DataIn("index", "Index", TypeRef.Int32);
            PinDefinition item = PinDefinition.DataIn("item", "Item", element);

            yield return new CreateListNode($"list.{suffix}.create", $"Create List ({label})", list);
            yield return new ListStatementNode($"list.{suffix}.add", $"Add to List ({label})", "Adds the item to the end of the list. Generates list.Add(item).",
                [listIn, item], context => new IrExpressionStatement(new IrInstanceCall(context.Input("list"), "Add", [context.Input("item")], TypeRef.Void)));
            yield return new ListStatementNode($"list.{suffix}.set", $"Set Item ({label})", "Replaces the item at Index. Index 0 is the first item. Generates list[index] = item.",
                [listIn, index, item], context => new IrElementAssignment(context.Input("list"), context.Input("index"), context.Input("item")));
            yield return new ListStatementNode($"list.{suffix}.remove-at", $"Remove Item At ({label})", "Removes the item at Index; later items move down by one. Generates list.RemoveAt(index).",
                [listIn, index], context => new IrExpressionStatement(new IrInstanceCall(context.Input("list"), "RemoveAt", [context.Input("index")], TypeRef.Void)));
            yield return new ListStatementNode($"list.{suffix}.clear", $"Clear List ({label})", "Removes every item. Generates list.Clear().",
                [listIn], context => new IrExpressionStatement(new IrInstanceCall(context.Input("list"), "Clear", [], TypeRef.Void)));

            yield return new ListValueNode($"list.{suffix}.get", $"Get Item ({label})", "The item at Index. Index 0 is the first item. Generates list[index].",
                [listIn, index, PinDefinition.DataOut("item", "Item", element)], context => new IrElementAccess(context.Input("list"), context.Input("index"), element));
            yield return new ListValueNode($"list.{suffix}.count", $"Count ({label})", "The number of items in the list. Generates list.Count.",
                [listIn, PinDefinition.DataOut("count", "Count", TypeRef.Int32)], context => new IrMemberAccess(context.Input("list"), "Count", TypeRef.Int32));
            yield return new ListValueNode($"list.{suffix}.contains", $"Contains ({label})", "True when the list has an item equal to Item. Generates list.Contains(item).",
                [listIn, item, PinDefinition.DataOut("result", "Result", TypeRef.Boolean)],
                context => new IrInstanceCall(context.Input("list"), "Contains", [context.Input("item")], TypeRef.Boolean));

            yield return new ForEachLoopNode($"flow.foreach.{suffix}", $"For Each ({label})", list);
        }

        TypeRef strings = TypeRef.ListOf(TypeRef.String);
        yield return new ListValueNode("string.split", "Split Text", "Splits Text at every Separator into a new list of strings. Generates new List<string>(text.Split(separator)).",
            [PinDefinition.DataIn("text", "Text", TypeRef.String), PinDefinition.DataIn("separator", "Separator", TypeRef.String), PinDefinition.DataOut("parts", "Parts", strings)],
            context => new IrObjectCreation(strings, [new IrInstanceCall(context.Input("text"), "Split", [context.Input("separator")], TypeRef.String)]),
            category: "String");
        yield return new ListValueNode("string.join", "Join Text", "Joins the strings of a list, with Separator between them. Generates string.Join(separator, list).",
            [PinDefinition.DataIn("separator", "Separator", TypeRef.String), PinDefinition.DataIn("list", "List", strings), PinDefinition.DataOut("text", "Text", TypeRef.String)],
            context => new IrStaticCall(TypeRef.String, "Join", [context.Input("separator"), context.Input("list")], TypeRef.String),
            category: "String");
    }
}

/// <summary>
/// Creates an empty list. A statement node, so each run creates exactly one list, however many nodes use it.
/// Generates <c>var list = new List&lt;T&gt;();</c>.
/// </summary>
public sealed class CreateListNode(string typeId, string displayName, TypeRef listType) : StatementNodeDefinition(
    typeId,
    displayName,
    CollectionNodes.Category,
    $"Creates a new, empty {listType}. Generates new {listType}().",
    [PinDefinition.ExecutionIn(), PinDefinition.ExecutionOut(), PinDefinition.DataOut("list", "List", listType)])
{
    public override void Lower(StatementLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.DeclareOutput("list", new IrObjectCreation(listType, []), "list");
    }
}

/// <summary>A list operation that changes the list, run in execution order.</summary>
public sealed class ListStatementNode(
    string typeId,
    string displayName,
    string description,
    IReadOnlyList<PinDefinition> dataPins,
    Func<StatementLoweringContext, IrStatement> lower) : StatementNodeDefinition(
        typeId,
        displayName,
        CollectionNodes.Category,
        description,
        [PinDefinition.ExecutionIn(), .. dataPins, PinDefinition.ExecutionOut()])
{
    public override void Lower(StatementLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Emit(lower(context));
    }
}

/// <summary>A value read from a list or text, inlined where it is used.</summary>
public sealed class ListValueNode(
    string typeId,
    string displayName,
    string description,
    IReadOnlyList<PinDefinition> pins,
    Func<ExpressionLoweringContext, IrExpression> lower,
    string category = CollectionNodes.Category) : PureNodeDefinition(typeId, displayName, category, description, pins)
{
    public override IrExpression Lower(ExpressionLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return lower(context);
    }
}

/// <summary>Runs Loop Body once per item of a list, then Completed. Generates <c>foreach (T item in list)</c>.</summary>
public sealed class ForEachLoopNode(string typeId, string displayName, TypeRef listType) : StatementNodeDefinition(
    typeId,
    displayName,
    "Flow",
    "Runs Loop Body once for each item of the list, in order, with Item set to that item. Then runs Completed. The list must not change while the loop runs. Generates a foreach statement.",
    [
        PinDefinition.ExecutionIn(),
        PinDefinition.DataIn("list", "List", listType),
        PinDefinition.LoopBody(),
        PinDefinition.DataOut("item", "Item", listType.ElementType!),
        PinDefinition.ExecutionOut("completed", "Completed"),
    ])
{
    public override void Lower(StatementLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IrExpression list = context.Input("list");
        IrLocal item = context.NewLocal("item", listType.ElementType!);
        context.Emit(new IrForEach(item, list, context.LowerLoopBody("body", ("item", item))));
    }
}
