using VisualStudioLikeIDE.Compiler.TypeSystem;

namespace VisualStudioLikeIDE.Compiler.Ir;

/// <summary>
/// A statement in the intermediate representation. <see cref="SourceNodeId"/> records the graph node that produced it,
/// which drives the node-to-code source map.
/// </summary>
public abstract record IrStatement
{
    public string? SourceNodeId { get; init; }
}

public sealed record IrExpressionStatement(IrExpression Expression) : IrStatement;

public sealed record IrLocalDeclaration(IrLocal Local, IrExpression Initializer) : IrStatement;

/// <summary>Assigns a new value to an existing local: <c>score = value;</c>.</summary>
public sealed record IrAssignment(IrLocal Local, IrExpression Value) : IrStatement;

public sealed record IrIf(IrExpression Condition, IrBlock Then, IrBlock Else) : IrStatement;

public sealed record IrReturn(IrExpression? Value) : IrStatement;

public sealed record IrWhile(IrExpression Condition, IrBlock Body) : IrStatement;

/// <summary><c>for (int index = start; index &lt; end; index++) { body }</c>. <paramref name="End"/> is evaluated before every iteration.</summary>
public sealed record IrFor(IrLocal Index, IrExpression Start, IrExpression End, IrBlock Body) : IrStatement;

public sealed record IrBreak : IrStatement;

public sealed record IrContinue : IrStatement;

/// <summary><c>throw new InvalidOperationException(message);</c></summary>
public sealed record IrThrow(IrExpression Message) : IrStatement;

/// <summary>
/// <c>try { Try } catch (Exception exception) { Catch } finally { Finally }</c>. <paramref name="Exception"/> is null when the catch
/// block does not use the exception; the finally clause is omitted when <paramref name="Finally"/> is empty.
/// </summary>
public sealed record IrTry(IrBlock Try, IrLocal? Exception, IrBlock Catch, IrBlock Finally) : IrStatement;

/// <summary>An ordered list of statements forming one C# block.</summary>
public sealed class IrBlock
{
    public List<IrStatement> Statements { get; } = [];

    public bool IsEmpty => Statements.Count == 0;

    /// <summary>True when control never falls through the end of the block.</summary>
    public bool EndsWithJump => Statements.Count > 0 && Statements[^1] is IrReturn or IrBreak or IrContinue or IrThrow;
}

public sealed record IrParameter(string Name, TypeRef Type);

public sealed record IrMethod(
    string Name,
    TypeRef ReturnType,
    IReadOnlyList<IrParameter> Parameters,
    IrBlock Body,
    string SourceGraphId,
    bool IsPrivate = false);

/// <summary>A static class generated from one graph.</summary>
/// <param name="IsPartial">True when other generated files add methods to the same class.</param>
public sealed record IrClass(string Namespace, string Name, IReadOnlyList<IrMethod> Methods, string SourceGraphId, bool IsPartial = false);
