namespace VisualStudioLikeIDE.Compiler.Ir;

/// <summary>
/// Semantics-preserving clean-ups that make generated code read like code a person would write.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>if (c) { } else { B }</c> becomes <c>if (!c) { B }</c>.</item>
/// <item><c>if (c) { A; return; } else { B }</c> becomes <c>if (c) { A; return; } B</c>.</item>
/// <item>An <c>if</c> with two empty arms is removed when its condition has no side effects.</item>
/// <item>A trailing <c>return;</c> at the end of a void method is removed.</item>
/// <item>A trailing <c>continue;</c> at the end of a loop body is removed.</item>
/// </list>
/// Hoisting statements out of an <c>else</c> widens the scope of their locals; this is safe because local names are unique per method.
/// </remarks>
public static class IrSimplifier
{
    public static void Simplify(IrMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        SimplifyBlock(method.Body);

        List<IrStatement> statements = method.Body.Statements;
        if (method.ReturnType == TypeSystem.TypeRef.Void && statements.Count > 0 && statements[^1] is IrReturn { Value: null })
        {
            statements.RemoveAt(statements.Count - 1);
        }
    }

    private static void SimplifyBlock(IrBlock block)
    {
        List<IrStatement> statements = block.Statements;
        for (int i = 0; i < statements.Count; i++)
        {
            if (statements[i] is IrWhile { Body: var whileBody })
            {
                SimplifyLoopBody(whileBody);
            }
            else if (statements[i] is IrFor { Body: var forBody })
            {
                SimplifyLoopBody(forBody);
            }
            else if (statements[i] is IrForEach { Body: var forEachBody })
            {
                SimplifyLoopBody(forEachBody);
            }
            else if (statements[i] is IrTry attempt)
            {
                SimplifyBlock(attempt.Try);
                SimplifyBlock(attempt.Catch);
                SimplifyBlock(attempt.Finally);
            }

            if (statements[i] is not IrIf branch)
            {
                continue;
            }

            SimplifyBlock(branch.Then);
            SimplifyBlock(branch.Else);

            if (branch.Then.IsEmpty && branch.Else.IsEmpty && !HasSideEffects(branch.Condition))
            {
                statements.RemoveAt(i);
                i--;
                continue;
            }

            if (branch.Then.IsEmpty && !branch.Else.IsEmpty)
            {
                branch = branch with { Condition = Negate(branch.Condition), Then = branch.Else, Else = new IrBlock() };
                statements[i] = branch;
            }

            if (branch.Then.EndsWithJump && !branch.Else.IsEmpty)
            {
                statements.InsertRange(i + 1, branch.Else.Statements);
                statements[i] = branch with { Else = new IrBlock() };
            }
        }
    }

    private static void SimplifyLoopBody(IrBlock body)
    {
        SimplifyBlock(body);
        if (body.Statements.Count > 0 && body.Statements[^1] is IrContinue)
        {
            body.Statements.RemoveAt(body.Statements.Count - 1);
        }
    }

    private static IrExpression Negate(IrExpression condition) => condition switch
    {
        IrUnary { Operator: IrUnaryOperator.LogicalNot } not => not.Operand,
        IrBinary { Operator: IrBinaryOperator.Equal } equal => equal with { Operator = IrBinaryOperator.NotEqual },
        IrBinary { Operator: IrBinaryOperator.NotEqual } notEqual => notEqual with { Operator = IrBinaryOperator.Equal },

        // Relational operators are not inverted: !(a < b) and a >= b differ when a or b is NaN.
        _ => new IrUnary(IrUnaryOperator.LogicalNot, condition, condition.Type),
    };

    private static bool HasSideEffects(IrExpression expression) => expression switch
    {
        // A property getter is code that may have side effects.
        IrStaticCall or IrInstanceCall or IrCall or IrMemberAccess or IrObjectCreation or IrElementAccess or IrError => true,

        // Integer division and remainder throw DivideByZeroException.
        IrBinary { Operator: IrBinaryOperator.Divide or IrBinaryOperator.Remainder } => true,
        IrBinary binary => HasSideEffects(binary.Left) || HasSideEffects(binary.Right),
        IrUnary unary => HasSideEffects(unary.Operand),
        IrConvert convert => HasSideEffects(convert.Operand),
        IrWith copy => HasSideEffects(copy.Receiver) || HasSideEffects(copy.Value),
        _ => false,
    };
}
