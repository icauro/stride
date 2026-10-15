using System.Globalization;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static bool IsStockPackageNumeric(Type type)
        => type.IsIntegral() || type == typeof(float) || type == typeof(double);

    private static bool StepPackageNumeric(Type type, Type declared, object? value,
        PropertyNumericRange? range, ref string text)
    {
        if (Nullable.GetUnderlyingType(declared) is not { } underlying || !IsStockPackageNumeric(underlying))
            return NativeNumericInput.StepText(type, value, false, range, ref text);
        // Original NumericValueNodeUpdater does not manufacture type/byte tuples
        // for Nullable<T>. NumericTextBox's absent-step defaults are 1 and 10.
        if (!NativeNumericInput.TryGesture(type, range, out var amount,
            range?.DeclaredSmallStep ?? 1, range?.DeclaredLargeStep ?? 10)) return false;
        object next;
        if (value == null)
        {
            // Mirror the real nullable NumericTextBox baseline; conversion errors
            // retain the source rather than inventing an underlying default value.
            var minimum = range?.Minimum ?? double.MinValue;
            var maximum = range?.Maximum ?? double.MaxValue;
            var baseline = amount > 0 ? minimum : maximum;
            var number = Math.Clamp(baseline + amount, minimum, maximum);
            next = Convert.ChangeType(number, type, CultureInfo.InvariantCulture);
            next = range?.Normalize(next) ?? next;
        }
        else next = NativeNumericInput.Step(type, value, range, amount);
        text = Convert.ToString(next, CultureInfo.InvariantCulture) ?? "";
        return true;
    }
}
