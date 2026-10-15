using System.Globalization;
using Hexa.NET.ImGui;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Updaters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal static class NativeNumericInput
{
    internal static PropertyNumericRange Combine(Type type, IEnumerable<PropertyNumericRange?> source) => Merge(type, source);
    internal static bool TrySliderGesture(Type type, PropertyNumericRange range, object? value, out double updated)
    {
        updated = 0;
        if (!Gui.IsItemFocused() && !Gui.IsItemActive()) return false;
        var io = Gui.GetIO(); if (io.KeyCtrl || io.KeyAlt || io.KeySuper) return false;
        if (Gui.IsKeyPressed(ImGuiKey.Home)) { updated = range.Minimum!.Value; return true; }
        if (Gui.IsKeyPressed(ImGuiKey.End)) { updated = range.Maximum!.Value; return true; }
        var increase = Gui.IsKeyPressed(ImGuiKey.UpArrow) || Gui.IsKeyPressed(ImGuiKey.RightArrow) || Gui.IsKeyPressed(ImGuiKey.PageUp);
        var decrease = Gui.IsKeyPressed(ImGuiKey.DownArrow) || Gui.IsKeyPressed(ImGuiKey.LeftArrow) || Gui.IsKeyPressed(ImGuiKey.PageDown);
        if (!increase && !decrease) return false;
        var step = Gui.IsKeyPressed(ImGuiKey.PageUp) || Gui.IsKeyPressed(ImGuiKey.PageDown) ? range.DeclaredLargeStep ?? 10 : range.DeclaredSmallStep ?? 1;
        if (!double.IsFinite(step) || step <= 0) return false;
        updated = Convert.ToDouble(Step(type, value, range, increase ? step : -step), CultureInfo.InvariantCulture); return true;
    }
    internal static PropertyNumericRange Merge(Type type, IEnumerable<PropertyNumericRange?> source)
    {
        var ranges = source.ToArray();
        double? Common(Func<PropertyNumericRange, double?> read)
        {
            var value = ranges.Length == 0 || ranges[0] == null ? null : read(ranges[0]!);
            return ranges.Any(range => (range == null ? null : read(range)) != value) ? null : value;
        }
        var places = ranges.FirstOrDefault()?.DecimalPlaces;
        if (ranges.Any(range => range?.DecimalPlaces != places)) places = null;
        return new(new PropertyNumericMetadata(Common(range => range.Minimum), Common(range => range.Maximum),
            Common(range => range.DeclaredSmallStep) ?? 1,
            Common(range => range.DeclaredLargeStep) ?? (type == typeof(byte) || type == typeof(sbyte) ? 5 : 10), places), type, places != null);
    }

    internal static bool StepText(Type type, object? value, bool mixed, PropertyNumericRange? range, ref string text)
    {
        if (!TryGesture(type, range, out var amount)) return false;
        var result = Step(type, mixed ? null : value, range, amount);
        text = Convert.ToString(result, CultureInfo.InvariantCulture) ?? "";
        return true;
    }
    internal static bool StepValue<T>(ref T value, PropertyNumericRange? range = null)
    {
        if (!TryGesture(typeof(T), range, out var amount)) return false;
        value = (T)Step(typeof(T), value, range, amount); return true;
    }
    internal static bool TryGesture(Type type, PropertyNumericRange? range, out double amount,
        double? smallStep = null, double? largeStep = null)
    {
        amount = 0;
        if (!Gui.IsItemActive() || !AssetSettingNumericMetadata.Supports(type)) return false;
        var io = Gui.GetIO();
        if (io.KeyCtrl || io.KeyAlt || io.KeySuper) return false;
        var increase = Gui.IsKeyPressed(ImGuiKey.UpArrow) || Gui.IsKeyPressed(ImGuiKey.PageUp);
        var decrease = Gui.IsKeyPressed(ImGuiKey.DownArrow) || Gui.IsKeyPressed(ImGuiKey.PageDown);
        if (!increase && !decrease) return false;
        var large = io.KeyShift || Gui.IsKeyPressed(ImGuiKey.PageUp) || Gui.IsKeyPressed(ImGuiKey.PageDown);
        var step = large ? largeStep ?? range?.DeclaredLargeStep ?? (type == typeof(byte) || type == typeof(sbyte) ? 5 : 10)
            : smallStep ?? range?.DeclaredSmallStep ?? 1;
        if (!double.IsFinite(step) || step <= 0) return false;
        amount = increase ? step : -step; return true;
    }

    internal static object Step(Type type, object? value, PropertyNumericRange? range, double amount)
    {
        if (type == typeof(float) || type == typeof(double))
        {
            var minimum = Math.Max(range?.Minimum ?? double.MinValue, Convert.ToDouble(NumericValueNodeUpdater.GetMinimum(type), CultureInfo.InvariantCulture));
            var maximum = Math.Min(range?.Maximum ?? double.MaxValue, Convert.ToDouble(NumericValueNodeUpdater.GetMaximum(type), CultureInfo.InvariantCulture));
            var baseline = value == null ? amount > 0 ? minimum : maximum : Convert.ToDouble(value, CultureInfo.InvariantCulture);
            var number = Math.Clamp(baseline + amount, minimum, maximum);
            object result = type == typeof(float) ? (object)(float)number : number;
            return range?.Normalize(result) ?? result;
        }
        var typeMinimum = Convert.ToDecimal(NumericValueNodeUpdater.GetMinimum(type), CultureInfo.InvariantCulture);
        var typeMaximum = Convert.ToDecimal(NumericValueNodeUpdater.GetMaximum(type), CultureInfo.InvariantCulture);
        decimal Bound(double? candidate, decimal fallback) => candidate == null ? fallback
            : candidate >= (double)decimal.MaxValue ? decimal.MaxValue : candidate <= (double)decimal.MinValue ? decimal.MinValue : (decimal)candidate.Value;
        var lower = Math.Max(typeMinimum, Bound(range?.Minimum, typeMinimum));
        var upper = Math.Min(typeMaximum, Bound(range?.Maximum, typeMaximum));
        var current = value == null ? amount > 0 ? lower : upper : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        var delta = Bound(amount, 0);
        // Avoid overflowing decimal before the native range coercion can run.
        var stepped = delta > 0 && current > decimal.MaxValue - delta ? decimal.MaxValue
            : delta < 0 && current < decimal.MinValue - delta ? decimal.MinValue : current + delta;
        var converted = Convert.ChangeType(Math.Clamp(stepped, lower, upper), type, CultureInfo.InvariantCulture);
        return range?.Normalize(converted) ?? converted;
    }

}
