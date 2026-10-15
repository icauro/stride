using System.Globalization;
using System.Runtime.CompilerServices;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal static class NativeRangedNumericControl
{
    private sealed class Draft { internal object? Observed; internal bool Mixed; internal double Number; internal bool Dirty; internal double? Minimum; internal double? Maximum; }
    private static readonly ConditionalWeakTable<object, Dictionary<uint, Draft>> drafts = new();
    internal static bool Matches(Type declaredType, PropertyNumericRange? range)
        => Nullable.GetUnderlyingType(declaredType) == null && AssetSettingNumericMetadata.Supports(declaredType)
            && range is { Minimum: not null, Maximum: not null, DeclaredSmallStep: not null, DeclaredLargeStep: not null };

    internal static unsafe bool Draw(object owner, Type type, object? value, bool mixed, IReadOnlyList<PropertyNumericRange?> ranges,
        float scale, out object? updated, out bool finished)
    {
        updated = mixed ? null : value; finished = false;
        if (ranges.Count == 0 || ranges.Any(range => !Matches(type, range))) return false;
        var range = ranges[0]!; var minimum = range.Minimum!.Value; var maximum = range.Maximum!.Value;
        var enabled = minimum < maximum && ranges.All(other => other!.Minimum == range.Minimum && other.Maximum == range.Maximum);
        var available = Gui.GetContentRegionAvail().X; var spacing = Gui.GetStyle().ItemSpacing.X;
        var textWidth = Math.Max(1, Math.Min(Math.Max(1, available - spacing - 1), Math.Max(60 * scale, (available - spacing) * .25f)));
        Gui.SetNextItemWidth(Math.Max(1, available - textWidth - spacing));
        var states = drafts.GetValue(owner, _ => new()); var id = Gui.GetID("##RangedSlider");
        if (!states.TryGetValue(id, out var draft)) states[id] = draft = new() { Number = mixed || value == null ? 0 : Convert.ToDouble(value, CultureInfo.InvariantCulture) };
        if (!Equals(value, draft.Observed) || mixed != draft.Mixed || draft.Minimum != minimum || draft.Maximum != maximum)
        { draft.Number = mixed || value == null ? 0 : Convert.ToDouble(value, CultureInfo.InvariantCulture); draft.Dirty = false; }
        draft.Observed = value; draft.Mixed = mixed; draft.Minimum = minimum; draft.Maximum = maximum;
        var number = draft.Number;
        Gui.BeginDisabled(!enabled);
        var changed = Gui.SliderScalar("##RangedSlider", ImGuiDataType.Double, &number, &minimum, &maximum, string.Empty, ImGuiSliderFlags.NoInput);
        if (enabled && NativeNumericInput.TrySliderGesture(type, range, draft.Dirty ? number : mixed ? null : value, out var stepped))
        { number = stepped; changed = true; finished = true; }
        finished |= Gui.IsItemDeactivatedAfterEdit(); Gui.EndDisabled();
        if (changed) { draft.Number = number; draft.Dirty = true; }
        if (draft.Dirty) updated = NativeNumericInput.Step(type, draft.Number, range, 0);
        var commit = draft.Dirty && finished && enabled;
        if (commit) draft.Dirty = false;
        Gui.SameLine(); Gui.SetNextItemWidth(textWidth);
        return commit;
    }
}
