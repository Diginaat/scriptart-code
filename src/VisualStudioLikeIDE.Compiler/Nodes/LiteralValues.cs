using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using VisualStudioLikeIDE.Compiler.Ir;
using VisualStudioLikeIDE.Compiler.TypeSystem;

namespace VisualStudioLikeIDE.Compiler.Nodes;

/// <summary>
/// Parses property text into literal values. Text is always interpreted with the invariant culture,
/// so a graph means the same thing on every machine.
/// </summary>
public static class LiteralValues
{
    /// <summary>
    /// The value a variable of <paramref name="type"/> starts with, given its initial value text: a literal, or a new empty list
    /// for a list type, whose text must be blank. Null with an <paramref name="error"/> when the text does not fit the type.
    /// </summary>
    public static IrExpression? TryInitialValue(TypeRef type, string text, out string? error)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(text);
        if (type.IsList)
        {
            error = string.IsNullOrWhiteSpace(text) ? null : "A list always starts empty. Leave the initial value blank and add items with Add to List.";
            return error is null ? new IrObjectCreation(type, []) : null;
        }

        return TryParse(type, text, out object? value, out error) ? new IrLiteral(value, type) : null;
    }

    public static bool TryParse(TypeRef type, string text, [NotNullWhen(true)] out object? value, out string? error)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(text);
        value = null;
        error = null;

        if (type == TypeRef.String)
        {
            value = text;
            return true;
        }

        if (type == TypeRef.Boolean)
        {
            if (bool.TryParse(text.Trim(), out bool boolean))
            {
                value = boolean;
                return true;
            }

            error = $"'{text}' is not a boolean. Use true or false.";
            return false;
        }

        if (type == TypeRef.Int32)
        {
            if (int.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int integer))
            {
                value = integer;
                return true;
            }

            error = $"'{text}' is not an integer between {int.MinValue} and {int.MaxValue}.";
            return false;
        }

        if (type == TypeRef.Double)
        {
            if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number))
            {
                value = number;
                return true;
            }

            error = $"'{text}' is not a finite number. Use '.' as the decimal separator.";
            return false;
        }

        error = $"Values of type {type} cannot be edited as text.";
        return false;
    }
}
