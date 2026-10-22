using VisualStudioLikeIDE.Compiler.Ir;
using VisualStudioLikeIDE.Compiler.Lowering;
using VisualStudioLikeIDE.Compiler.TypeSystem;

namespace VisualStudioLikeIDE.Compiler.Nodes;

/// <summary>
/// Nodes for <c>List&lt;T&gt;</c> and <c>Dictionary&lt;TKey, TValue&gt;</c>. Pins are typed, and the type system has no generic
/// pins, so list nodes of the simple types are registered once per element type, like the arithmetic nodes: <c>list.int.add</c>,
/// <c>list.string.add</c>, and so on. Lists of classes and structs and every dictionary have too many possible types to register;
/// their nodes (<c>list.add</c>, <c>dictionary.set</c>, …) name the collection type in <see cref="CollectionProperty"/> and are
/// built per node by <see cref="NodeLibrary.Resolve"/>.
/// </summary>
public static class CollectionNodes
{
    public const string Category = "Lists";

    public const string DictionaryCategory = "Dictionaries";

    /// <summary>The node property holding the C# name of the list or dictionary type a generated node works on.</summary>
    public const string CollectionProperty = "collection";

    private static readonly (TypeRef Type, string Suffix, string Label)[] Elements =
    [
        (TypeRef.Boolean, "bool", "Boolean"),
        (TypeRef.Int32, "int", "Integer"),
        (TypeRef.Double, "double", "Number"),
        (TypeRef.String, "string", "String"),
    ];

    private static readonly Dictionary<string, string> NoProperties = [];

    private static readonly HashSet<string> GeneratedTypeIds = new(StringComparer.Ordinal)
    {
        "list.create", "list.add", "list.set", "list.remove-at", "list.clear", "list.get", "list.count", "list.contains", "flow.foreach",
        "dictionary.create", "dictionary.set", "dictionary.remove", "dictionary.clear", "dictionary.get", "dictionary.contains-key",
        "dictionary.count", "dictionary.keys", "dictionary.values",
    };

    /// <summary>True for the node types whose pins come from the collection type in <see cref="CollectionProperty"/>.</summary>
    public static bool IsGeneratedNode(string typeId) => GeneratedTypeIds.Contains(typeId);

    /// <summary>True when the nodes of <paramref name="collection"/> are generated rather than registered.</summary>
    public static bool HasGeneratedNodes(TypeRef collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        return collection.IsDictionary || (collection.IsList && !TypeRef.ElementTypes.Contains(collection.ElementType!));
    }

    public static IEnumerable<NodeDefinition> All()
    {
        foreach ((TypeRef element, string suffix, string label) in Elements)
        {
            foreach (NodeDefinition node in ListNodes(TypeRef.ListOf(element), label, operation => operation == "foreach" ? $"flow.foreach.{suffix}" : $"list.{suffix}.{operation}", NoProperties))
            {
                yield return node;
            }
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

    /// <summary>
    /// The generated nodes of a list of a class or struct, or of a dictionary, each recording <paramref name="collection"/> in
    /// <see cref="CollectionProperty"/>. Empty for a type whose nodes are registered.
    /// </summary>
    public static IEnumerable<NodeDefinition> For(TypeRef collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (!HasGeneratedNodes(collection))
        {
            return [];
        }

        var properties = new Dictionary<string, string> { [CollectionProperty] = collection.SourceName };
        return collection.IsList
            ? ListNodes(collection, collection.ElementType!.DisplayName, operation => operation == "foreach" ? "flow.foreach" : $"list.{operation}", properties)
            : DictionaryNodes(collection, properties);
    }

    private static IEnumerable<NodeDefinition> ListNodes(TypeRef list, string label, Func<string, string> typeId, IReadOnlyDictionary<string, string> properties)
    {
        TypeRef element = list.ElementType!;
        PinDefinition listIn = PinDefinition.DataIn("list", "List", list);
        PinDefinition index = PinDefinition.DataIn("index", "Index", TypeRef.Int32);
        PinDefinition item = PinDefinition.DataIn("item", "Item", element);

        yield return new CreateListNode(typeId("create"), $"Create List ({label})", list) { InitialProperties = properties };
        yield return new ListStatementNode(typeId("add"), $"Add to List ({label})", "Adds the item to the end of the list. Generates list.Add(item).",
            [listIn, item], context => new IrExpressionStatement(new IrInstanceCall(context.Input("list"), "Add", [context.Input("item")], TypeRef.Void))) { InitialProperties = properties };
        yield return new ListStatementNode(typeId("set"), $"Set Item ({label})", "Replaces the item at Index. Index 0 is the first item. Generates list[index] = item.",
            [listIn, index, item], context => new IrElementAssignment(context.Input("list"), context.Input("index"), context.Input("item"))) { InitialProperties = properties };
        yield return new ListStatementNode(typeId("remove-at"), $"Remove Item At ({label})", "Removes the item at Index; later items move down by one. Generates list.RemoveAt(index).",
            [listIn, index], context => new IrExpressionStatement(new IrInstanceCall(context.Input("list"), "RemoveAt", [context.Input("index")], TypeRef.Void))) { InitialProperties = properties };
        yield return new ListStatementNode(typeId("clear"), $"Clear List ({label})", "Removes every item. Generates list.Clear().",
            [listIn], context => new IrExpressionStatement(new IrInstanceCall(context.Input("list"), "Clear", [], TypeRef.Void))) { InitialProperties = properties };

        yield return new ListValueNode(typeId("get"), $"Get Item ({label})", "The item at Index. Index 0 is the first item. Generates list[index].",
            [listIn, index, PinDefinition.DataOut("item", "Item", element)], context => new IrElementAccess(context.Input("list"), context.Input("index"), element)) { InitialProperties = properties };
        yield return new ListValueNode(typeId("count"), $"Count ({label})", "The number of items in the list. Generates list.Count.",
            [listIn, PinDefinition.DataOut("count", "Count", TypeRef.Int32)], context => new IrMemberAccess(context.Input("list"), "Count", TypeRef.Int32)) { InitialProperties = properties };
        yield return new ListValueNode(typeId("contains"), $"Contains ({label})", "True when the list has an item equal to Item. Generates list.Contains(item).",
            [listIn, item, PinDefinition.DataOut("result", "Result", TypeRef.Boolean)],
            context => new IrInstanceCall(context.Input("list"), "Contains", [context.Input("item")], TypeRef.Boolean)) { InitialProperties = properties };

        yield return new ForEachLoopNode(typeId("foreach"), $"For Each ({label})", list) { InitialProperties = properties };
    }

    private static IEnumerable<NodeDefinition> DictionaryNodes(TypeRef dictionary, IReadOnlyDictionary<string, string> properties)
    {
        TypeRef key = dictionary.KeyType!;
        TypeRef value = dictionary.ElementType!;
        string label = $"{key}, {value}";
        PinDefinition dictionaryIn = PinDefinition.DataIn("dictionary", "Dictionary", dictionary);
        PinDefinition keyIn = PinDefinition.DataIn("key", "Key", key);

        yield return new CreateListNode("dictionary.create", $"Create Dictionary ({label})", dictionary, DictionaryCategory, "dictionary", "Dictionary") { InitialProperties = properties };
        yield return new ListStatementNode("dictionary.set", $"Set Entry ({label})", "Stores Value under Key, replacing the value already stored there. Generates dictionary[key] = value.",
            [dictionaryIn, keyIn, PinDefinition.DataIn("value", "Value", value)],
            context => new IrElementAssignment(context.Input("dictionary"), context.Input("key"), context.Input("value")), DictionaryCategory) { InitialProperties = properties };
        yield return new ListStatementNode("dictionary.remove", $"Remove Entry ({label})", "Removes Key and its value; nothing happens when Key is missing. Generates dictionary.Remove(key).",
            [dictionaryIn, keyIn], context => new IrExpressionStatement(new IrInstanceCall(context.Input("dictionary"), "Remove", [context.Input("key")], TypeRef.Boolean)), DictionaryCategory) { InitialProperties = properties };
        yield return new ListStatementNode("dictionary.clear", $"Clear Dictionary ({label})", "Removes every entry. Generates dictionary.Clear().",
            [dictionaryIn], context => new IrExpressionStatement(new IrInstanceCall(context.Input("dictionary"), "Clear", [], TypeRef.Void)), DictionaryCategory) { InitialProperties = properties };

        // Never fails: a missing key gives Default instead of stopping the program with KeyNotFoundException.
        yield return new ListValueNode("dictionary.get", $"Get Value ({label})", "The value stored under Key, or Default when Key is missing. Generates dictionary.GetValueOrDefault(key, fallback).",
            [dictionaryIn, keyIn, PinDefinition.DataIn("default", "Default", value), PinDefinition.DataOut("value", "Value", value)],
            context => new IrInstanceCall(context.Input("dictionary"), "GetValueOrDefault", [context.Input("key"), context.Input("default")], value), DictionaryCategory) { InitialProperties = properties };
        yield return new ListValueNode("dictionary.contains-key", $"Contains Key ({label})", "True when a value is stored under Key. Generates dictionary.ContainsKey(key).",
            [dictionaryIn, keyIn, PinDefinition.DataOut("result", "Result", TypeRef.Boolean)],
            context => new IrInstanceCall(context.Input("dictionary"), "ContainsKey", [context.Input("key")], TypeRef.Boolean), DictionaryCategory) { InitialProperties = properties };
        yield return new ListValueNode("dictionary.count", $"Count ({label})", "The number of entries. Generates dictionary.Count.",
            [dictionaryIn, PinDefinition.DataOut("count", "Count", TypeRef.Int32)],
            context => new IrMemberAccess(context.Input("dictionary"), "Count", TypeRef.Int32), DictionaryCategory) { InitialProperties = properties };

        // Copies, so a For Each over them may change the dictionary. The member access is typed as the list it is copied into: C#
        // has a key collection type, but the graph type system does not, and the access is never stored on its own.
        yield return new ListValueNode("dictionary.keys", $"Keys ({label})", "A new list of every key, for example to loop over with For Each. Generates new List<TKey>(dictionary.Keys).",
            [dictionaryIn, PinDefinition.DataOut("keys", "Keys", TypeRef.ListOf(key))],
            context => new IrObjectCreation(TypeRef.ListOf(key), [new IrMemberAccess(context.Input("dictionary"), "Keys", TypeRef.ListOf(key))]), DictionaryCategory) { InitialProperties = properties };
        yield return new ListValueNode("dictionary.values", $"Values ({label})", "A new list of every value. Generates new List<TValue>(dictionary.Values).",
            [dictionaryIn, PinDefinition.DataOut("values", "Values", TypeRef.ListOf(value))],
            context => new IrObjectCreation(TypeRef.ListOf(value), [new IrMemberAccess(context.Input("dictionary"), "Values", TypeRef.ListOf(value))]), DictionaryCategory) { InitialProperties = properties };
    }
}

/// <summary>
/// Creates an empty list or dictionary. A statement node, so each run creates exactly one, however many nodes use it.
/// Generates <c>var list = new List&lt;T&gt;();</c>.
/// </summary>
public sealed class CreateListNode(
    string typeId,
    string displayName,
    TypeRef listType,
    string category = CollectionNodes.Category,
    string pinId = "list",
    string pinName = "List") : StatementNodeDefinition(
    typeId,
    displayName,
    category,
    $"Creates a new, empty {listType}. Generates new {listType}().",
    [PinDefinition.ExecutionIn(), PinDefinition.ExecutionOut(), PinDefinition.DataOut(pinId, pinName, listType)])
{
    public override void Lower(StatementLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.DeclareOutput(pinId, new IrObjectCreation(listType, []), pinId);
    }
}

/// <summary>A list or dictionary operation that changes the collection, run in execution order.</summary>
public sealed class ListStatementNode(
    string typeId,
    string displayName,
    string description,
    IReadOnlyList<PinDefinition> dataPins,
    Func<StatementLoweringContext, IrStatement> lower,
    string category = CollectionNodes.Category) : StatementNodeDefinition(
        typeId,
        displayName,
        category,
        description,
        [PinDefinition.ExecutionIn(), .. dataPins, PinDefinition.ExecutionOut()])
{
    public override void Lower(StatementLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Emit(lower(context));
    }
}

/// <summary>A value read from a list, dictionary, or text, inlined where it is used.</summary>
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
