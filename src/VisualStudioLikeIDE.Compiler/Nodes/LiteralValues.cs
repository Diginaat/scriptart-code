using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using VisualStudioLikeIDE.Compiler.TypeSystem;

namespace VisualStudioLikeIDE.Compiler.Nodes;

/// <summary>
/// Parses property text into literal values. Text is always interpreted with the invariant culture,
/// so a graph means the same thing on every machine.
/// </summary>
public static class LiteralValues
{
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
