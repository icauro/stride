using System.Globalization;
using Stride.Core.Annotations;

namespace Stride.Editor.ImGui;

/// <summary>Cached scalar numeric metadata and WPF-compatible input normalization.</summary>
internal sealed record PropertyNumericMetadata(double? Minimum = null, double? Maximum = null, double? SmallStep = null,
    double? LargeStep = null, int? DecimalPlaces = null);

internal sealed class PropertyNumericRange(PropertyNumericMetadata metadata, Type numericType, bool roundDecimalPlaces = true)
{
    public PropertyNumericRange(DataMemberRangeAttribute metadata, Type numericType, bool roundDecimalPlaces = true)
        : this(new PropertyNumericMetadata(metadata.Minimum, metadata.Maximum, metadata.SmallStep, metadata.LargeStep, metadata.DecimalPlaces), numericType, roundDecimalPlaces) { }
    public bool RoundsDecimalPlaces => roundDecimalPlaces;
    public double? Minimum => metadata.Minimum;
    public double? Maximum => metadata.Maximum;
    public double? DeclaredSmallStep => metadata.SmallStep;
    public double? DeclaredLargeStep => metadata.LargeStep;
    public int? DecimalPlaces => metadata.DecimalPlaces;
    public string Hint { get; } = metadata.Minimum is { } minimum && metadata.Maximum is { } maximum
        ? $"Range {minimum.ToString("G", CultureInfo.InvariantCulture)} to {maximum.ToString("G", CultureInfo.InvariantCulture)}"
        : metadata.Minimum is { } lower ? "Minimum " + lower.ToString("G", CultureInfo.InvariantCulture)
        : metadata.Maximum is { } upper ? "Maximum " + upper.ToString("G", CultureInfo.InvariantCulture) : "";
    public double SmallStep { get; } = metadata.SmallStep is { } small && double.IsFinite(small) && small > 0 ? small : 0;
    public double LargeStep { get; } = metadata.LargeStep is { } large && double.IsFinite(large) && large > 0 ? large : 0;
    public string? Format { get; } = roundDecimalPlaces && metadata.DecimalPlaces is { } places ? $"%.{Math.Clamp(places, 0, 15)}f" : null;

    public object? Normalize(object? value)
    {
        if (value is decimal fixedPoint)
        {
            decimal Bound(double? bound, decimal fallback)
            {
                if (bound == null) return fallback;
                if (!double.IsFinite(bound.Value)) throw new ArgumentException("Numeric range bounds must be finite.");
                if (bound >= (double)decimal.MaxValue) return decimal.MaxValue;
                if (bound <= (double)decimal.MinValue) return decimal.MinValue;
                return (decimal)bound.Value;
            }
            var minimum = Bound(metadata.Minimum, decimal.MinValue);
            var maximum = Bound(metadata.Maximum, decimal.MaxValue);
            if (minimum > maximum) throw new ArgumentException("The numeric range contains no values of this property type.");
            fixedPoint = Math.Clamp(fixedPoint, minimum, maximum);
            if (roundDecimalPlaces && metadata.DecimalPlaces is { } places) fixedPoint = decimal.Round(fixedPoint, Math.Clamp(places, 0, 28));
            return Math.Clamp(fixedPoint, minimum, maximum);
        }
        if (value is byte or sbyte or short or ushort or uint or long or ulong)
        {
            // Decimal represents every 64-bit integer exactly. Metadata uses
            // double, but the authored value never makes that precision-losing trip.
            var (typeMinimum, typeMaximum) = value switch
            {
                byte => ((decimal)byte.MinValue, (decimal)byte.MaxValue),
                sbyte => ((decimal)sbyte.MinValue, (decimal)sbyte.MaxValue),
                short => ((decimal)short.MinValue, (decimal)short.MaxValue),
                ushort => ((decimal)ushort.MinValue, (decimal)ushort.MaxValue),
                uint => ((decimal)uint.MinValue, (decimal)uint.MaxValue),
                long => ((decimal)long.MinValue, (decimal)long.MaxValue),
                _ => ((decimal)ulong.MinValue, (decimal)ulong.MaxValue),
            };
            decimal MetadataBound(double? bound, decimal fallback)
            {
                if (bound == null) return fallback;
                if (!double.IsFinite(bound.Value)) throw new ArgumentException("Numeric range bounds must be finite.");
                // Saturate unrepresentably large metadata before decimal conversion.
                if (bound >= (double)decimal.MaxValue) return decimal.MaxValue;
                if (bound <= (double)decimal.MinValue) return decimal.MinValue;
                return (decimal)bound.Value;
            }
            var minimum = Math.Max(typeMinimum, decimal.Ceiling(MetadataBound(metadata.Minimum, typeMinimum)));
            var maximum = Math.Min(typeMaximum, decimal.Floor(MetadataBound(metadata.Maximum, typeMaximum)));
            if (minimum > maximum) throw new ArgumentException("The numeric range contains no values of this property type.");
            return Convert.ChangeType(Math.Clamp(Convert.ToDecimal(value, CultureInfo.InvariantCulture), minimum, maximum), numericType, CultureInfo.InvariantCulture);
        }
        // Match NumericTextBox's explicit decimal rounding and inclusive range coercion.
        double NormalizeNumber(double number, double typeMinimum, double typeMaximum, bool integral)
        {
            var minimum = Math.Max(metadata.Minimum ?? typeMinimum, typeMinimum);
            var maximum = Math.Min(metadata.Maximum ?? typeMaximum, typeMaximum);
            if (integral) { minimum = Math.Ceiling(minimum); maximum = Math.Floor(maximum); }
            if (minimum > maximum) throw new ArgumentException("The numeric range contains no values of this property type.");
            number = Math.Clamp(number, minimum, maximum);
            if (integral) number = Math.Round(number, 0);
            else if (roundDecimalPlaces && metadata.DecimalPlaces is { } places) number = Math.Round(number, Math.Clamp(places, 0, 15));
            return Math.Clamp(number, minimum, maximum);
        }
        return value switch
        {
            float number => (object)(float)NormalizeNumber(number, -float.MaxValue, float.MaxValue, false),
            double number when numericType == typeof(float) => (float)NormalizeNumber(number, -float.MaxValue, float.MaxValue, false),
            double number when numericType == typeof(int) => checked((int)NormalizeNumber(number, int.MinValue, int.MaxValue, true)),
            double number => NormalizeNumber(number, -double.MaxValue, double.MaxValue, false),
            int number => checked((int)NormalizeNumber(number, int.MinValue, int.MaxValue, true)),
            _ => value,
        };
    }
}
