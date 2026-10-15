using System.Globalization;
using System.Runtime.CompilerServices;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal static class NativeTimeSpanInput
{
    private sealed class Draft { internal TimeSpan Value; internal bool Initialized, Mixed; internal readonly string[] Text = new string[4]; }
    private static readonly ConditionalWeakTable<object, Dictionary<uint, Draft>> drafts = new();
    private static readonly string[] Labels = [" d ", " h ", " min ", " s "];
    private static readonly PropertyNumericRange[] Ranges =
    [
        new(new PropertyNumericMetadata(0, int.MaxValue, 1, 10, 0), typeof(int)),
        new(new PropertyNumericMetadata(0, 23, 1, 10, 0), typeof(int)),
        new(new PropertyNumericMetadata(0, 59, 1, 10, 0), typeof(int)),
        new(new PropertyNumericMetadata(0, 59.9999999, 1, 10, 7), typeof(double)),
    ];
    internal static string ComponentLabel(int axis) => Labels[axis];
    internal static PropertyNumericRange ComponentRange(int axis) => Ranges[axis];
    internal static double Component(TimeSpan value, int axis) => axis switch
    {
        0 => value.Days, 1 => value.Hours, 2 => value.Minutes,
        _ => (double)(value.Ticks % TimeSpan.TicksPerMinute) / TimeSpan.TicksPerSecond,
    };
    internal static TimeSpan ReplaceComponent(TimeSpan current, int axis, double coordinate)
    {
        if (!double.IsFinite(coordinate)) throw new ArgumentException("The duration component must be finite.");
        var ticks = axis switch
        {
            0 => checked((long)coordinate * TimeSpan.TicksPerDay + current.Hours * TimeSpan.TicksPerHour + current.Minutes * TimeSpan.TicksPerMinute + current.Ticks % TimeSpan.TicksPerMinute),
            1 => checked(current.Days * TimeSpan.TicksPerDay + (long)coordinate * TimeSpan.TicksPerHour + current.Minutes * TimeSpan.TicksPerMinute + current.Ticks % TimeSpan.TicksPerMinute),
            2 => checked(current.Days * TimeSpan.TicksPerDay + current.Hours * TimeSpan.TicksPerHour + (long)coordinate * TimeSpan.TicksPerMinute + current.Ticks % TimeSpan.TicksPerMinute),
            _ => checked(current.Days * TimeSpan.TicksPerDay + current.Hours * TimeSpan.TicksPerHour + current.Minutes * TimeSpan.TicksPerMinute + (long)(coordinate * TimeSpan.TicksPerSecond)),
        };
        return TimeSpan.FromTicks(ticks);
    }
    internal static bool Draw(object owner, TimeSpan value, bool mixed, bool canEdit, out TimeSpan updated, out bool finished)
    {
        updated = value; finished = false;
        var id = Gui.GetID("##TimeSpan"); var states = drafts.GetValue(owner, _ => new());
        if (!states.TryGetValue(id, out var draft)) states[id] = draft = new();
        var components = new double[] { value.Days, value.Hours, value.Minutes, (double)(value.Ticks % TimeSpan.TicksPerMinute) / TimeSpan.TicksPerSecond };
        if (!draft.Initialized || draft.Value != value || draft.Mixed != mixed)
        {
            draft.Initialized = true; draft.Value = value; draft.Mixed = mixed;
            for (var axis = 0; axis < 4; axis++) draft.Text[axis] = mixed ? "" : components[axis].ToString(axis == 3 ? "F7" : "F0", CultureInfo.InvariantCulture);
        }
        var changed = false;
        Gui.BeginGroup(); Gui.BeginDisabled(mixed);
        var available = Math.Max(1, Gui.GetContentRegionAvail().X - Labels.Sum(label => Gui.CalcTextSize(label).X) - Gui.GetStyle().ItemSpacing.X * 7);
        for (var axis = 0; axis < 4; axis++)
        {
            if (axis > 0) Gui.SameLine();
            Gui.PushID(axis); Gui.SetNextItemWidth(Math.Max(1, available * (axis == 3 ? 3 : 2) / 9));
            var text = draft.Text[axis];
            var submitted = Gui.InputTextWithHint("##Time", mixed ? "×" : "", ref text, 256,
                ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll
                    | (canEdit && !mixed ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.ReadOnly));
            var feedback = Gui.GetID("##Time");
            var deactivated = Gui.IsItemDeactivatedAfterEdit();
            draft.Text[axis] = text;
            if (canEdit && !mixed)
            {
                try
                {
                    var type = axis == 3 ? typeof(double) : typeof(int);
                    if (NativeNumericInput.StepText(type, components[axis], false, Ranges[axis], ref text))
                    { submitted = true; draft.Text[axis] = text; }
                    if (submitted || deactivated)
                    {
                        var coordinate = PropertyConversionFeedback.Convert(feedback, () => Convert.ToDouble(Ranges[axis].Normalize(double.Parse(text, CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture));
                        if (!double.IsFinite(coordinate)) throw new ArgumentException("The duration component must be finite.");
                        // Original TimeSpanEditor replaces only the touched coordinate;
                        // all untouched seconds retain their exact existing ticks.
                        updated = ReplaceComponent(updated, axis, coordinate); changed = true; finished = true;
                        PropertyConversionFeedback.Validate(feedback);
                    }
                }
                catch (Exception error) when (error is FormatException or OverflowException or ArgumentException)
                {
                    PropertyConversionFeedback.Fail(feedback, error);
                    draft.Text[axis] = components[axis].ToString(axis == 3 ? "F7" : "F0", CultureInfo.InvariantCulture);
                }
            }
            PropertyConversionFeedback.Draw(feedback);
            Gui.PopID(); Gui.SameLine(); Gui.TextUnformatted(Labels[axis]);
        }
        Gui.EndDisabled(); Gui.EndGroup(); return changed;
    }
}
