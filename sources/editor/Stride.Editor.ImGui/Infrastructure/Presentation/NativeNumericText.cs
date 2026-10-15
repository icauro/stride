using System.Globalization;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Updaters;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal static class NativeNumericText
{
    private const NumberStyles Syntax = NumberStyles.Any & ~NumberStyles.AllowThousands;
    private static IEnumerable<CultureInfo> Cultures()
    {
        yield return CultureInfo.CurrentCulture;
        yield return CultureInfo.InvariantCulture;
        yield return CultureInfo.GetCultureInfo("de-DE");
    }
    private static bool Hexadecimal(string text, out ulong value, out bool prefixed)
    {
        var prefix = text.AsSpan().TrimStart('0');
        prefixed = prefix.StartsWith("x", StringComparison.OrdinalIgnoreCase) || prefix.StartsWith("#", StringComparison.OrdinalIgnoreCase);
        value = 0;
        return prefixed && ulong.TryParse(prefix.TrimStart(stackalloc char[] { 'x', '#' }),
            NumberStyles.HexNumber, NumberFormatInfo.InvariantInfo, out value);
    }
    internal static object Parse(string text, Type type, PropertyNumericRange? range = null)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (!type.IsNumeric()) throw new ArgumentException("The native numeric template requires a numeric type.", nameof(type));
        var hexadecimal = Hexadecimal(text, out var token, out var prefixed);
        if (prefixed && !hexadecimal) throw new FormatException("The numeric value is invalid.");
        if (type.IsIntegral() || type == typeof(decimal))
        {
            decimal number = token;
            var parsed = hexadecimal;
            if (!parsed)
                foreach (var culture in Cultures())
                    if (decimal.TryParse(text, Syntax, culture, out number)) { parsed = true; break; }
            if (!parsed)
            {
                // NumericTextBox also accepts exponents beyond decimal's span.
                // Coerce a real range before typed conversion, preserving exact
                // integral endpoint values instead of routing them through double.
                var wide = 0.0;
                foreach (var culture in Cultures())
                    if (double.TryParse(text, Syntax, culture, out wide)) { parsed = true; break; }
                if (!parsed) throw new FormatException("The numeric value is invalid.");
                var coerced = range?.Normalize(wide) ?? wide;
                wide = Convert.ToDouble(coerced, CultureInfo.InvariantCulture);
                if (range != null && type.IsIntegral())
                {
                    var minimum = Convert.ToDecimal(NumericValueNodeUpdater.GetMinimum(type), CultureInfo.InvariantCulture);
                    var maximum = Convert.ToDecimal(NumericValueNodeUpdater.GetMaximum(type), CultureInfo.InvariantCulture);
                    number = wide <= (double)minimum ? minimum : wide >= (double)maximum ? maximum : Convert.ToDecimal(wide);
                }
                else number = Convert.ToDecimal(wide);
            }
            number = (decimal)(range?.Normalize(number) ?? number);
            if (type.IsIntegral())
            {
                // IntEditorTemplate always sets DecimalPlaces=0. Decimal keeps
                // whole 64-bit inputs exact while preserving native midpoint rounding.
                number = decimal.Round(number, 0);
                if (range != null)
                    number = Math.Clamp(number,
                        Convert.ToDecimal(NumericValueNodeUpdater.GetMinimum(type), CultureInfo.InvariantCulture),
                        Convert.ToDecimal(NumericValueNodeUpdater.GetMaximum(type), CultureInfo.InvariantCulture));
            }
            return Convert.ChangeType(number, type, CultureInfo.InvariantCulture);
        }
        double floating = token;
        var accepted = hexadecimal;
        if (!accepted)
            foreach (var culture in Cultures())
                if (double.TryParse(text, Syntax, culture, out floating)) { accepted = true; break; }
        if (!accepted) throw new FormatException("The numeric value is invalid.");
        var normalized = range?.Normalize(floating) ?? floating;
        return Convert.ChangeType(normalized, type, CultureInfo.InvariantCulture);
    }
    internal static string Format(object? value, Type type, PropertyNumericRange? range = null)
    {
        if (value == null) return "";
        if ((Nullable.GetUnderlyingType(type) ?? type).IsIntegral())
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        if (range?.DecimalPlaces is { } places)
        {
            if (value is decimal exact) return decimal.Round(exact, Math.Clamp(places, 0, 28)).ToString(CultureInfo.InvariantCulture);
            return Math.Round(Convert.ToDouble(value, CultureInfo.InvariantCulture), Math.Clamp(places, 0, 15)).ToString(CultureInfo.InvariantCulture);
        }
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }
}
