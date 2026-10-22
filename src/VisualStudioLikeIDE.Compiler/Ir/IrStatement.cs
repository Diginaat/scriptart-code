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

/// <summary><c>foreach (T item in collection) { body }</c>.</summary>
public sealed record IrForEach(IrLocal Item, IrExpression Collection, IrBlock Body) : IrStatement;

/// <summary>Replaces an element of a list: <c>list[index] = value;</c>.</summary>
public sealed record IrElementAssignment(IrExpression Receiver, IrExpression Index, IrExpression Value) : IrStatement;

/// <summary>Assigns a property of an object: <c>point.X = value;</c>.</summary>
public sealed record IrMemberAssignment(IrExpression Receiver, string MemberName, IrExpression Value) : IrStatement;

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

/// <summary>A property with a public getter and setter, and the value it starts with; no initializer when that is the type's default.</summary>
public sealed record IrProperty(string Name, TypeRef Type, IrExpression? Initializer);

public enum IrClassKind
{
    /// <summary>A static class holding methods, generated from the entry graph and the function graphs.</summary>
    Static,

    /// <summary>A sealed class holding properties.</summary>
    Class,

    /// <summary>A struct holding properties.</summary>
    Struct,
}

/// <summary>A type generated from one graph: a static class with methods, or a class or struct with properties.</summary>
/// <param name="IsPartial">True when other generated files add methods to the same class.</param>
public sealed record IrClass(
    string Namespace,
    string Name,
    IReadOnlyList<IrMethod> Methods,
    string SourceGraphId,
    bool IsPartial = false,
    IrClassKind Kind = IrClassKind.Static,
    IReadOnlyList<IrProperty>? Properties = null)
{
    public IReadOnlyList<IrProperty> Properties { get; init; } = Properties ?? [];
}
