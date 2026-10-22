using VisualStudioLikeIDE.Compiler.TypeSystem;

namespace VisualStudioLikeIDE.Compiler.Ir;

/// <summary>
/// A value-producing expression in the intermediate representation. The IR is independent of
/// the graph file format, the editor, and Roslyn.
/// </summary>
public abstract record IrExpression(TypeRef Type);

/// <summary>A constant. <see cref="Value"/> is a <see cref="bool"/>, <see cref="int"/>, <see cref="double"/>, or <see cref="string"/>.</summary>
public sealed record IrLiteral(object Value, TypeRef Type) : IrExpression(Type);

public enum IrBinaryOperator
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Remainder,
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    LogicalAnd,
    LogicalOr,
    Coalesce,
}

public sealed record IrBinary(IrBinaryOperator Operator, IrExpression Left, IrExpression Right, TypeRef Type)
    : IrExpression(Type);

public enum IrUnaryOperator
{
    LogicalNot,
    Negate,
}

public sealed record IrUnary(IrUnaryOperator Operator, IrExpression Operand, TypeRef Type) : IrExpression(Type);

/// <summary>A call to a static method, for example <c>Console.WriteLine(value)</c>.</summary>
public sealed record IrStaticCall(TypeRef DeclaringType, string MethodName, IReadOnlyList<IrExpression> Arguments, TypeRef Type)
    : IrExpression(Type);

/// <summary>A call to another method of the generated class, for example <c>Square(value)</c>.</summary>
public sealed record IrCall(string MethodName, IReadOnlyList<IrExpression> Arguments, TypeRef Type) : IrExpression(Type);

/// <summary>A call to an instance method, for example <c>value.ToString()</c>.</summary>
public sealed record IrInstanceCall(IrExpression Receiver, string MethodName, IReadOnlyList<IrExpression> Arguments, TypeRef Type)
    : IrExpression(Type);

/// <summary>Creates an object: <c>new List&lt;int&gt;()</c>.</summary>
public sealed record IrObjectCreation(TypeRef Type, IReadOnlyList<IrExpression> Arguments) : IrExpression(Type);

/// <summary>Reads an element of a list: <c>list[index]</c>.</summary>
public sealed record IrElementAccess(IrExpression Receiver, IrExpression Index, TypeRef Type) : IrExpression(Type);

/// <summary>Reads a property or field of a value, for example <c>exception.Message</c>.</summary>
public sealed record IrMemberAccess(IrExpression Receiver, string MemberName, TypeRef Type) : IrExpression(Type);

public sealed record IrLocalReference(IrLocal Local) : IrExpression(Local.Type);

/// <summary>An implicit, lossless conversion inserted where a pin of one type feeds a pin of another.</summary>
public sealed record IrConvert(IrExpression Operand, TypeRef Type) : IrExpression(Type);

/// <summary>Stands in for an expression that could not be lowered. Never emitted: its presence implies an error diagnostic.</summary>
public sealed record IrError(TypeRef Type) : IrExpression(Type);

/// <summary>A local variable. Names are unique within their method.</summary>
public sealed record IrLocal(string Name, TypeRef Type);
