using ScriptArt.Compiler.Diagnostics;
using ScriptArt.Compiler.Nodes;
using ScriptArt.Compiler.TypeSystem;

namespace ScriptArt.Compiler.Lowering;

/// <summary>The outcome of checking whether two pins may be connected.</summary>
public readonly record struct ConnectionCheck(string? ErrorId, string? Error)
{
    public static readonly ConnectionCheck Allowed = new(null, null);

    public bool IsAllowed => Error is null;
}

/// <summary>
/// The rules for a single connection. Shared by the editor (live feedback while dragging) and the compiler (validation).
/// </summary>
public static class ConnectionRules
{
    /// <param name="baseTypesOf">
    /// The classes a type derives from (<see cref="NodeLibrary.BaseTypesOf"/>); a value then also connects to inputs of those. Without
    /// it, only the conversions of <see cref="TypeConversions"/> apply.
    /// </param>
    public static ConnectionCheck Check(PinDefinition from, PinDefinition to, Func<TypeRef, IReadOnlyList<TypeRef>>? baseTypesOf = null)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        if (from.Direction != PinDirection.Output || to.Direction != PinDirection.Input)
        {
            return new(DiagnosticIds.InvalidConnection, "Connections run from an output pin to an input pin.");
        }

        if (from.IsExecution != to.IsExecution)
        {
            return new(DiagnosticIds.InvalidConnection, "Execution pins can only connect to execution pins, and data pins to data pins.");
        }

        if (from.DataType is { } sourceType && to.DataType is { } targetType
            && !TypeConversions.IsImplicitlyConvertible(sourceType, targetType)
            && baseTypesOf?.Invoke(sourceType).Contains(targetType) != true)
        {
            return new(DiagnosticIds.TypeMismatch, $"Cannot connect {sourceType} to {targetType}. Add a conversion node.");
        }

        return ConnectionCheck.Allowed;
    }

    /// <summary>
    /// True when the pin accepts at most one connection: data inputs (one value) and execution outputs (one next statement).
    /// Execution inputs and data outputs may have many.
    /// </summary>
    public static bool IsSingleConnection(PinDefinition pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        return pin.IsExecution ? pin.Direction == PinDirection.Output : pin.Direction == PinDirection.Input;
    }
}
