using VisualStudioLikeIDE.Compiler.Diagnostics;
using VisualStudioLikeIDE.Compiler.Ir;
using VisualStudioLikeIDE.Compiler.Lowering;
using VisualStudioLikeIDE.Compiler.TypeSystem;

namespace VisualStudioLikeIDE.Compiler.Nodes;

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

        yield return new ConsoleWriteNode("console.write-line", "Print Line", "WriteLine", "Writes text followed by a line break to the console.");
        yield return new ConsoleWriteNode("console.write", "Print", "Write", "Writes text to the console without a line break.");
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

        yield return new BinaryOperatorNode("string.equals", "Equal (String)", "Comparison", IrBinaryOperator.Equal, TypeRef.String, TypeRef.Boolean, "True when both strings contain the same characters.");
        yield return new BinaryOperatorNode("string.concat", "Concatenate", "String", IrBinaryOperator.Add, TypeRef.String, TypeRef.String, "Joins two strings.");

        yield return new BinaryOperatorNode("logic.and", "And", "Logic", IrBinaryOperator.LogicalAnd, TypeRef.Boolean, TypeRef.Boolean, "True when both inputs are true. B is evaluated only when A is true.");
        yield return new BinaryOperatorNode("logic.or", "Or", "Logic", IrBinaryOperator.LogicalOr, TypeRef.Boolean, TypeRef.Boolean, "True when either input is true. B is evaluated only when A is false.");
        yield return new UnaryOperatorNode("logic.not", "Not", "Logic", IrUnaryOperator.LogicalNot, TypeRef.Boolean, "Inverts a boolean.");
        yield return new UnaryOperatorNode("math.int.negate", "Negate (Integer)", "Math", IrUnaryOperator.Negate, TypeRef.Int32, "Returns the value with its sign reversed.");
        yield return new UnaryOperatorNode("math.double.negate", "Negate (Number)", "Math", IrUnaryOperator.Negate, TypeRef.Double, "Returns the value with its sign reversed.");

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

    private static IEnumerable<NodeDefinition> ArithmeticNodes(TypeRef type, string suffix, string label)
    {
        string divideDescription = type == TypeRef.Int32
            ? "Returns A / B, discarding any remainder. Throws DivideByZeroException when B is zero."
            : "Returns A / B.";
        yield return new BinaryOperatorNode($"math.{suffix}.add", $"Add ({label})", "Math", IrBinaryOperator.Add, type, type, "Returns A + B.");
        yield return new BinaryOperatorNode($"math.{suffix}.subtract", $"Subtract ({label})", "Math", IrBinaryOperator.Subtract, type, type, "Returns A - B.");
        yield return new BinaryOperatorNode($"math.{suffix}.multiply", $"Multiply ({label})", "Math", IrBinaryOperator.Multiply, type, type, "Returns A * B.");
        yield return new BinaryOperatorNode($"math.{suffix}.divide", $"Divide ({label})", "Math", IrBinaryOperator.Divide, type, type, divideDescription);
        yield return new BinaryOperatorNode($"math.{suffix}.remainder", $"Remainder ({label})", "Math", IrBinaryOperator.Remainder, type, type, "Returns the remainder of A / B.");
    }

    private static IEnumerable<NodeDefinition> ComparisonNodes(TypeRef type, string suffix, string label)
    {
        yield return new BinaryOperatorNode($"compare.{suffix}.equal", $"Equal ({label})", "Comparison", IrBinaryOperator.Equal, type, TypeRef.Boolean, "True when A == B.");
        yield return new BinaryOperatorNode($"compare.{suffix}.not-equal", $"Not Equal ({label})", "Comparison", IrBinaryOperator.NotEqual, type, TypeRef.Boolean, "True when A != B.");
        yield return new BinaryOperatorNode($"compare.{suffix}.less", $"Less ({label})", "Comparison", IrBinaryOperator.LessThan, type, TypeRef.Boolean, "True when A < B.");
        yield return new BinaryOperatorNode($"compare.{suffix}.less-or-equal", $"Less or Equal ({label})", "Comparison", IrBinaryOperator.LessThanOrEqual, type, TypeRef.Boolean, "True when A <= B.");
        yield return new BinaryOperatorNode($"compare.{suffix}.greater", $"Greater ({label})", "Comparison", IrBinaryOperator.GreaterThan, type, TypeRef.Boolean, "True when A > B.");
        yield return new BinaryOperatorNode($"compare.{suffix}.greater-or-equal", $"Greater or Equal ({label})", "Comparison", IrBinaryOperator.GreaterThanOrEqual, type, TypeRef.Boolean, "True when A >= B.");
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
    "Runs True or False depending on the condition. Generates an if statement.",
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
    "Stops the current method.",
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
    "Reads the next line of input from the console. Returns an empty string at the end of input.",
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
        $"A constant {type} value.",
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
        $"Converts a {sourceType} to text using the current culture.",
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
        $"Converts text to a {targetType} using the current culture. Throws FormatException when the text is not a valid {targetType}.",
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
/// Runs Try; if it throws, runs Catch with the error's message; then always runs Finally. Generates
/// <c>try { } catch (Exception exception) { } finally { }</c>.
/// </summary>
public sealed class TryNode() : StatementNodeDefinition(
    "flow.try",
    "Try",
    "Flow",
    "Runs Try. If it fails with an exception, runs Catch, where Message describes the error. Finally always runs last, even after Return or Break. Then runs Completed. Generates try, catch, and finally.",
    [
        PinDefinition.ExecutionIn(),
        PinDefinition.Block("try", "Try"),
        PinDefinition.Block("catch", "Catch"),
        PinDefinition.DataOut("message", "Message", TypeRef.String),
        PinDefinition.Block("finally", "Finally"),
        PinDefinition.ExecutionOut("completed", "Completed"),
    ])
{
    public override void Lower(StatementLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IrBlock attempt = context.LowerBlock("try", NestedBlock.Try);

        // The exception gets a name only when the Catch block reads its message.
        IrLocal? exception = null;
        IrBlock handler;
        if (context.IsOutputUsed("message"))
        {
            exception = context.NewLocal("exception", TypeRef.Exception);
            IrLocal message = context.NewLocal("message", TypeRef.String);
            handler = context.LowerBlock("catch", NestedBlock.Catch, ("message", message));
            var read = new IrLocalDeclaration(message, new IrMemberAccess(new IrLocalReference(exception), "Message", TypeRef.String));
            handler.Statements.Insert(0, read with { SourceNodeId = context.NodeId });
        }
        else
        {
            handler = context.LowerBlock("catch", NestedBlock.Catch);
        }

        context.Emit(new IrTry(attempt, exception, handler, context.LowerBlock("finally", NestedBlock.Finally)));
    }
}

/// <summary>Stops with an error. Generates <c>throw new InvalidOperationException(message);</c>.</summary>
public sealed class ThrowNode() : StatementNodeDefinition(
    "flow.throw",
    "Throw Error",
    "Flow",
    "Stops with an error that has the given message. A surrounding Try node's Catch handles it; otherwise the program ends with the error. Generates throw.",
    [
        PinDefinition.ExecutionIn(),
        PinDefinition.DataIn("message", "Message", TypeRef.String),
    ])
{
    public override void Lower(StatementLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Emit(new IrThrow(context.Input("message")));
    }
}
