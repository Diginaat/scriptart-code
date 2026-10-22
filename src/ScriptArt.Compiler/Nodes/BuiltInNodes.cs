using ScriptArt.Compiler.Diagnostics;
using ScriptArt.Compiler.Ir;
using ScriptArt.Compiler.Lowering;
using ScriptArt.Compiler.TypeSystem;

namespace ScriptArt.Compiler.Nodes;

/// <summary>
/// The node definitions shipped with the IDE.
/// </summary>
public static class BuiltInNodes
{
    public const string EntryTypeId = "core.entry";

    public static IEnumerable<NodeDefinition> All()
    {
        yield return new EntryNode();
        yield return new BranchNode();
        yield return new ReturnNode();
        yield return new WhileLoopNode();
        yield return new ForLoopNode();
        yield return new LoopJumpNode("flow.break", "Break", "Leaves the innermost loop and continues after it. Generates break.", new IrBreak());
        yield return new LoopJumpNode("flow.continue", "Continue", "Skips the rest of the loop body and starts the next iteration. Generates continue.", new IrContinue());
        yield return new TryNode();
        yield return new ThrowNode();
        yield return new RethrowNode();

        yield return new ConsoleWriteNode("console.write-line", "Print Line", "WriteLine", "Shows Text in the console window and moves to the next line, so the next output starts on a new line. Example: Hello becomes a line Hello.");
        yield return new ConsoleWriteNode("console.write", "Print", "Write", "Shows Text in the console window and stays on the same line, so the next output continues right after it. Use Print Line to end the line.");
        yield return new ConsoleReadLineNode();

        yield return new LiteralNode("literal.string", "String", TypeRef.String, "Hello, World!");
        yield return new LiteralNode("literal.int", "Integer", TypeRef.Int32, "0");
        yield return new LiteralNode("literal.double", "Number", TypeRef.Double, "0.0");
        yield return new LiteralNode("literal.bool", "Boolean", TypeRef.Boolean, "true");

        foreach (NodeDefinition node in ArithmeticNodes(TypeRef.Int32, "int", "Integer")
            .Concat(ArithmeticNodes(TypeRef.Double, "double", "Number"))
            .Concat(ComparisonNodes(TypeRef.Int32, "int", "Integer"))
            .Concat(ComparisonNodes(TypeRef.Double, "double", "Number")))
        {
            yield return node;
        }

        yield return new BinaryOperatorNode("string.equals", "Equal (String)", "Comparison", IrBinaryOperator.Equal, TypeRef.String, TypeRef.Boolean, "True when A and B are exactly the same text, including upper and lower case and spaces. Example: \"cat\" and \"Cat\" are not equal.");
        yield return new BinaryOperatorNode("string.concat", "Concatenate", "String", IrBinaryOperator.Add, TypeRef.String, TypeRef.String, "Puts B directly after A to make one text. Example: \"Hello, \" and \"Ada\" give \"Hello, Ada\". Add spaces yourself where you want them.");

        yield return new BinaryOperatorNode("logic.and", "And", "Logic", IrBinaryOperator.LogicalAnd, TypeRef.Boolean, TypeRef.Boolean, "True only when A and B are both true. B is only worked out when A is true. Example: age >= 18 And has ticket.");
        yield return new BinaryOperatorNode("logic.or", "Or", "Logic", IrBinaryOperator.LogicalOr, TypeRef.Boolean, TypeRef.Boolean, "True when A or B (or both) is true. B is only worked out when A is false. Example: is weekend Or is holiday.");
        yield return new UnaryOperatorNode("logic.not", "Not", "Logic", IrUnaryOperator.LogicalNot, TypeRef.Boolean, "Turns true into false and false into true.");
        yield return new UnaryOperatorNode("math.int.negate", "Negate (Integer)", "Math", IrUnaryOperator.Negate, TypeRef.Int32, "The value with its sign flipped: 5 becomes -5 and -5 becomes 5.");
        yield return new UnaryOperatorNode("math.double.negate", "Negate (Number)", "Math", IrUnaryOperator.Negate, TypeRef.Double, "The value with its sign flipped: 5 becomes -5 and -5 becomes 5.");

        yield return new ToStringNode("convert.int-to-string", "Integer to String", TypeRef.Int32);
        yield return new ToStringNode("convert.double-to-string", "Number to String", TypeRef.Double);
        yield return new ToStringNode("convert.bool-to-string", "Boolean to String", TypeRef.Boolean);
        yield return new ParseNode("convert.string-to-int", "Parse Integer", TypeRef.Int32);
        yield return new ParseNode("convert.string-to-double", "Parse Number", TypeRef.Double);

        foreach (NodeDefinition node in CollectionNodes.All())
        {
            yield return node;
        }
    }

    /// <summary>The exception types a Try node can catch, by C# name. Each catch also handles the types derived from its type.</summary>
    public static IReadOnlyList<TypeRef> CatchableExceptions { get; } =
    [
        TypeRef.Exception,
        new("System", "ArgumentException"),
        new("System", "ArgumentOutOfRangeException"),
        new("System", "DivideByZeroException"),
        new("System", "FormatException"),
        TypeRef.InvalidOperationException,
        new("System.Collections.Generic", "KeyNotFoundException"),
        new("System", "NotSupportedException"),
        new("System", "OverflowException"),
        new("System", "TimeoutException"),
        new("System.IO", "IOException"),
        new("System.IO", "FileNotFoundException"),
    ];

    /// <summary>The exception types Throw Error can throw: those whose constructor takes just a message.</summary>
    public static IReadOnlyList<TypeRef> ThrowableExceptions { get; } =
        [.. CatchableExceptions.Where(type => type.Name is not ("ArgumentOutOfRangeException" or "DivideByZeroException" or "OverflowException"))];

    internal static TypeRef ExceptionType(IReadOnlyList<TypeRef> types, string name) => types.FirstOrDefault(type => type.Name == name) ?? types[0];

    private static IEnumerable<NodeDefinition> ArithmeticNodes(TypeRef type, string suffix, string label)
    {
        string divideDescription = type == TypeRef.Int32
            ? "A divided by B as a whole number; the remainder is dropped. Example: 7 / 2 gives 3. Stops the program with DivideByZeroException when B is 0."
            : "A divided by B. Example: 7 / 2 gives 3.5.";
        yield return new BinaryOperatorNode($"math.{suffix}.add", $"Add ({label})", "Math", IrBinaryOperator.Add, type, type, "A plus B. Example: 2 + 3 gives 5.");
        yield return new BinaryOperatorNode($"math.{suffix}.subtract", $"Subtract ({label})", "Math", IrBinaryOperator.Subtract, type, type, "A minus B. Example: 5 - 3 gives 2.");
        yield return new BinaryOperatorNode($"math.{suffix}.multiply", $"Multiply ({label})", "Math", IrBinaryOperator.Multiply, type, type, "A times B. Example: 4 * 3 gives 12.");
        yield return new BinaryOperatorNode($"math.{suffix}.divide", $"Divide ({label})", "Math", IrBinaryOperator.Divide, type, type, divideDescription);
        yield return new BinaryOperatorNode($"math.{suffix}.remainder", $"Remainder ({label})", "Math", IrBinaryOperator.Remainder, type, type, "What is left after dividing A by B. Example: 7 % 3 gives 1. Useful to test even numbers: n % 2 is 0.");
    }

    private static IEnumerable<NodeDefinition> ComparisonNodes(TypeRef type, string suffix, string label)
    {
        yield return new BinaryOperatorNode($"compare.{suffix}.equal", $"Equal ({label})", "Comparison", IrBinaryOperator.Equal, type, TypeRef.Boolean, "True when A and B are the same value.");
        yield return new BinaryOperatorNode($"compare.{suffix}.not-equal", $"Not Equal ({label})", "Comparison", IrBinaryOperator.NotEqual, type, TypeRef.Boolean, "True when A and B are different values.");
        yield return new BinaryOperatorNode($"compare.{suffix}.less", $"Less ({label})", "Comparison", IrBinaryOperator.LessThan, type, TypeRef.Boolean, "True when A is smaller than B. Example: 2 < 3 is true.");
        yield return new BinaryOperatorNode($"compare.{suffix}.less-or-equal", $"Less or Equal ({label})", "Comparison", IrBinaryOperator.LessThanOrEqual, type, TypeRef.Boolean, "True when A is smaller than or the same as B.");
        yield return new BinaryOperatorNode($"compare.{suffix}.greater", $"Greater ({label})", "Comparison", IrBinaryOperator.GreaterThan, type, TypeRef.Boolean, "True when A is bigger than B. Example: 3 > 2 is true.");
        yield return new BinaryOperatorNode($"compare.{suffix}.greater-or-equal", $"Greater or Equal ({label})", "Comparison", IrBinaryOperator.GreaterThanOrEqual, type, TypeRef.Boolean, "True when A is bigger than or the same as B.");
    }
}

/// <summary>Marks where a graph's generated method begins.</summary>
public sealed class EntryNode() : StatementNodeDefinition(
    BuiltInNodes.EntryTypeId,
    "Entry",
    "Flow",
    "The program starts here. Generates the body of Program.Main.",
    [PinDefinition.ExecutionOut()])
{
    public override void Lower(StatementLoweringContext context)
    {
        // The entry point has no statements of its own; the method body is everything after it.
    }
}

public sealed class BranchNode() : StatementNodeDefinition(
    "core.branch",
    "Branch",
    "Flow",
    "Makes a choice: when Condition is true the True wire runs, otherwise the False wire runs. Generates an if statement.",
    [
        PinDefinition.ExecutionIn(),
        PinDefinition.DataIn("condition", "Condition", TypeRef.Boolean),
        PinDefinition.ExecutionOut("true", "True"),
        PinDefinition.ExecutionOut("false", "False"),
    ])
{
    public override void Lower(StatementLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IrExpression condition = context.Input("condition");
        IrBlock whenTrue = context.LowerBranch("true");
        IrBlock whenFalse = context.LowerBranch("false");
        context.Emit(new IrIf(condition, whenTrue, whenFalse));
    }
}

public sealed class ReturnNode() : StatementNodeDefinition(
    "core.return",
    "Return",
    "Flow",
    "Stops this function or graph here; nothing after it runs.",
    [PinDefinition.ExecutionIn()])
{
    public override void Lower(StatementLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ReturnType != TypeRef.Void)
        {
            context.ReportError(DiagnosticIds.MissingReturn, $"This function returns {context.ReturnType}. Use a Return Value node and connect the value to return.");
            return;
        }

        if (context.IsInFinally)
        {
            context.ReportError(DiagnosticIds.JumpOutOfFinally, "Return cannot leave a Finally block. Put it after the Try node.");
            return;
        }

        context.Emit(new IrReturn(null));
    }
}

public sealed class WhileLoopNode() : StatementNodeDefinition(
    "flow.while",
    "While Loop",
    "Flow",
    "Runs Loop Body again and again while the condition is true; the condition is checked before every iteration. Then runs Completed. Generates a while statement.",
    [
        PinDefinition.ExecutionIn(),
        PinDefinition.DataIn("condition", "Condition", TypeRef.Boolean),
        PinDefinition.LoopBody(),
        PinDefinition.ExecutionOut("completed", "Completed"),
    ])
{
    public override bool EvaluatesInputsOnce => false;

    public override void Lower(StatementLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IrExpression condition = context.Input("condition");
        context.Emit(new IrWhile(condition, context.LowerLoopBody("body")));
    }
}

public sealed class ForLoopNode() : StatementNodeDefinition(
    "flow.for",
    "For Loop",
    "Flow",
    "Runs Loop Body once for each Index from Start up to, but not including, End; End is checked before every iteration. Then runs Completed. Generates a for statement.",
    [
        PinDefinition.ExecutionIn(),
        PinDefinition.DataIn("start", "Start", TypeRef.Int32),
        PinDefinition.DataIn("end", "End", TypeRef.Int32),
        PinDefinition.LoopBody(),
        PinDefinition.DataOut("index", "Index", TypeRef.Int32),
        PinDefinition.ExecutionOut("completed", "Completed"),
    ])
{
    public override bool EvaluatesInputsOnce => false;

    public override void Lower(StatementLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IrExpression start = context.Input("start");
        IrExpression end = context.Input("end");
        IrLocal index = context.NewLocal("i", TypeRef.Int32);
        context.Emit(new IrFor(index, start, end, context.LowerLoopBody("body", ("index", index))));
    }
}

/// <summary>Break or Continue: ends the current iteration and is only valid inside a loop body.</summary>
public sealed class LoopJumpNode(string typeId, string displayName, string description, IrStatement jump) : StatementNodeDefinition(
    typeId,
    displayName,
    "Flow",
    description,
    [PinDefinition.ExecutionIn()])
{
    public override void Lower(StatementLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsInLoopBody && context.IsInFinally)
        {
            context.ReportError(DiagnosticIds.JumpOutOfFinally, $"{DisplayName} cannot leave a Finally block. Only a loop inside the Finally block can use it.");
            return;
        }

        if (!context.IsInLoopBody)
        {
            context.ReportError(DiagnosticIds.JumpOutsideLoop, $"{DisplayName} must be inside the Loop Body of a While Loop or For Loop.");
            return;
        }

        context.Emit(jump);
    }
}

/// <summary>Generates <c>Console.WriteLine(text)</c> or <c>Console.Write(text)</c>.</summary>
public sealed class ConsoleWriteNode(string typeId, string displayName, string methodName, string description)
    : StatementNodeDefinition(
        typeId,
        displayName,
        "Console",
        description,
        [
            PinDefinition.ExecutionIn(),
            PinDefinition.DataIn("text", "Text", TypeRef.String),
            PinDefinition.ExecutionOut(),
        ])
{
    public override void Lower(StatementLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IrExpression text = context.Input("text");
        context.Emit(new IrExpressionStatement(new IrStaticCall(TypeRef.Console, methodName, [text], TypeRef.Void)));
    }
}

/// <summary>Generates <c>string line = Console.ReadLine() ?? "";</c>. End of input reads as an empty string.</summary>
public sealed class ConsoleReadLineNode() : StatementNodeDefinition(
    "console.read-line",
    "Read Line",
    "Console",
    "Waits until the user types a line in the console and presses Enter, then gives that text. Gives empty text when there is no more input.",
    [
        PinDefinition.ExecutionIn(),
        PinDefinition.ExecutionOut(),
        PinDefinition.DataOut("line", "Line", TypeRef.String),
    ])
{
    public override void Lower(StatementLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var readLine = new IrStaticCall(TypeRef.Console, "ReadLine", [], TypeRef.String);
        var lineOrEmpty = new IrBinary(IrBinaryOperator.Coalesce, readLine, new IrLiteral("", TypeRef.String), TypeRef.String);
        context.DeclareOutput("line", lineOrEmpty, "line", whenUnused: readLine);
    }
}

public sealed class LiteralNode(string typeId, string displayName, TypeRef type, string defaultValue)
    : PureNodeDefinition(
        typeId,
        displayName,
        "Literals",
        $"A fixed {type} value that you type in. Double-click the value on the node to change it.",
        [PinDefinition.DataOut("value", "Value", type)],
        [new PropertyDefinition("value", "Value", type, defaultValue)])
{
    public override IrExpression Lower(ExpressionLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.LiteralProperty("value");
    }
}

public sealed class BinaryOperatorNode(
    string typeId,
    string displayName,
    string category,
    IrBinaryOperator binaryOperator,
    TypeRef operandType,
    TypeRef resultType,
    string description)
    : PureNodeDefinition(
        typeId,
        displayName,
        category,
        description,
        [
            PinDefinition.DataIn("a", "A", operandType),
            PinDefinition.DataIn("b", "B", operandType),
            PinDefinition.DataOut("result", "Result", resultType),
        ])
{
    public override IrExpression Lower(ExpressionLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new IrBinary(binaryOperator, context.Input("a"), context.Input("b"), resultType);
    }
}

public sealed class UnaryOperatorNode(
    string typeId,
    string displayName,
    string category,
    IrUnaryOperator unaryOperator,
    TypeRef type,
    string description)
    : PureNodeDefinition(
        typeId,
        displayName,
        category,
        description,
        [
            PinDefinition.DataIn("value", "Value", type),
            PinDefinition.DataOut("result", "Result", type),
        ])
{
    public override IrExpression Lower(ExpressionLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new IrUnary(unaryOperator, context.Input("value"), type);
    }
}

/// <summary>Generates <c>value.ToString()</c>, which formats with the current culture.</summary>
public sealed class ToStringNode(string typeId, string displayName, TypeRef sourceType)
    : PureNodeDefinition(
        typeId,
        displayName,
        "Conversion",
        $"Turns a {sourceType} into text, for example to show it with Print Line. Uses the number format of this computer.",
        [
            PinDefinition.DataIn("value", "Value", sourceType),
            PinDefinition.DataOut("text", "Text", TypeRef.String),
        ])
{
    public override IrExpression Lower(ExpressionLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new IrInstanceCall(context.Input("value"), "ToString", [], TypeRef.String);
    }
}

/// <summary>Generates <c>int.Parse(text)</c> or <c>double.Parse(text)</c>, which use the current culture.</summary>
public sealed class ParseNode(string typeId, string displayName, TypeRef targetType)
    : PureNodeDefinition(
        typeId,
        displayName,
        "Conversion",
        $"Reads a {targetType} from text, such as typed input. Stops the program with FormatException when the text is not a valid {targetType}; catch it with a Try node.",
        [
            PinDefinition.DataIn("text", "Text", TypeRef.String),
            PinDefinition.DataOut("value", "Value", targetType),
        ])
{
    public override IrExpression Lower(ExpressionLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new IrStaticCall(targetType, "Parse", [context.Input("text")], targetType);
    }
}

/// <summary>
/// Runs Try; if it throws an exception of the type chosen in its Catches property (or a type derived from it), runs Catch with the
/// error's message; then always runs Finally. Generates <c>try { } catch (FormatException exception) { } finally { }</c>. An exception
/// of another type passes through to an enclosing Try node; nest Try nodes to handle several types differently.
/// </summary>
public sealed class TryNode() : StatementNodeDefinition(
    "flow.try",
    "Try",
    "Flow",
    "Runs Try. If it fails with an exception of the type in Catches, or a type derived from it, runs Catch, where Message describes the error. Finally always runs last, even after Return or Break. Then runs Completed. Generates try, catch, and finally.",
    [
        PinDefinition.ExecutionIn(),
        PinDefinition.Block("try", "Try"),
        PinDefinition.Block("catch", "Catch"),
        PinDefinition.DataOut("message", "Message", TypeRef.String),
        PinDefinition.Block("finally", "Finally"),
        PinDefinition.ExecutionOut("completed", "Completed"),
    ],
    [new PropertyDefinition("exception", "Catches", TypeRef.String, "Exception", [.. BuiltInNodes.CatchableExceptions.Select(type => type.Name)])])
{
    public override void Lower(StatementLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        TypeRef caught = BuiltInNodes.ExceptionType(BuiltInNodes.CatchableExceptions, context.Property("exception"));
        IrBlock attempt = context.LowerBlock("try", NestedBlock.Try);

        // The exception gets a name only when the Catch block reads its message.
        IrLocal? exception = null;
        IrBlock handler;
        if (context.IsOutputUsed("message"))
        {
            exception = context.NewLocal("exception", caught);
            IrLocal message = context.NewLocal("message", TypeRef.String);
            handler = context.LowerBlock("catch", NestedBlock.Catch, ("message", message));
            var read = new IrLocalDeclaration(message, new IrMemberAccess(new IrLocalReference(exception), "Message", TypeRef.String));
            handler.Statements.Insert(0, read with { SourceNodeId = context.NodeId });
        }
        else
        {
            handler = context.LowerBlock("catch", NestedBlock.Catch);
        }

        context.Emit(new IrTry(attempt, exception, handler, context.LowerBlock("finally", NestedBlock.Finally), caught));
    }
}

/// <summary>Stops with an error. Generates <c>throw new InvalidOperationException(message);</c>, or the exception type chosen in its Type property.</summary>
public sealed class ThrowNode() : StatementNodeDefinition(
    "flow.throw",
    "Throw Error",
    "Flow",
    "Stops with an error of the chosen type that has the given message. A surrounding Try node that catches the type handles it; otherwise the program ends with the error. Generates throw.",
    [
        PinDefinition.ExecutionIn(),
        PinDefinition.DataIn("message", "Message", TypeRef.String),
    ],
    [new PropertyDefinition("exception", "Type", TypeRef.String, "InvalidOperationException", [.. BuiltInNodes.ThrowableExceptions.Select(type => type.Name)])])
{
    public override void Lower(StatementLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        TypeRef type = BuiltInNodes.ExceptionType(BuiltInNodes.ThrowableExceptions, context.Property("exception"));
        context.Emit(new IrThrow(context.Input("message"), type));
    }
}

/// <summary>Throws the exception a Catch block is handling again, for an enclosing Try node or the caller. Generates <c>throw;</c>.</summary>
public sealed class RethrowNode() : StatementNodeDefinition(
    "flow.rethrow",
    "Rethrow",
    "Flow",
    "Inside a Catch block, throws the caught exception again with its original stack trace, so an enclosing Try node or the caller handles it. Generates throw;.",
    [PinDefinition.ExecutionIn()])
{
    public override void Lower(StatementLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsInCatch)
        {
            context.ReportError(DiagnosticIds.RethrowOutsideCatch, context.IsInFinally
                ? "Rethrow cannot be used in a Finally block. Put it in the Catch block."
                : "Rethrow must be inside the Catch block of a Try node.");
            return;
        }

        context.Emit(new IrRethrow());
    }
}
