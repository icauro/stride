using System.Globalization;
using System.Runtime.CompilerServices;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal static class NativeDateTimeInput
{
    private sealed class Draft { internal DateTime Value; internal bool Initialized, Mixed; internal readonly string[] Text = new string[6]; }
    private static readonly ConditionalWeakTable<object, Dictionary<uint, Draft>> drafts = new();
    private static readonly string[] Labels = [" y ", " m ", " d ", " h ", " min ", " s "];
    private static readonly PropertyNumericRange[] Ranges =
    [
        new(new PropertyNumericMetadata(1, 9999, 1, 10, 0), typeof(int)),
        new(new PropertyNumericMetadata(1, 12, 1, 10, 0), typeof(int)),
        new(new PropertyNumericMetadata(1, int.MaxValue, 1, 10, 0), typeof(int)),
        new(new PropertyNumericMetadata(0, 23, 1, 10, 0), typeof(int)),
        new(new PropertyNumericMetadata(0, 59, 1, 10, 0), typeof(int)),
        new(new PropertyNumericMetadata(0, 59.9999999, 1, 10, 7), typeof(double)),
    ];
    internal static string ComponentLabel(int axis) => Labels[axis];
    internal static PropertyNumericRange ComponentRange(int axis) => Ranges[axis];
    internal static double Component(DateTime value, int axis) => axis switch
    {
        0 => value.Year, 1 => value.Month, 2 => value.Day, 3 => value.Hour, 4 => value.Minute,
        _ => (double)(value.Ticks % TimeSpan.TicksPerMinute) / TimeSpan.TicksPerSecond,
    };
    internal static DateTime ReplaceComponent(DateTime current, int axis, double coordinate)
    {
        if (!double.IsFinite(coordinate)) throw new ArgumentException("The date/time component must be finite.");
        var year = axis == 0 ? (int)coordinate : current.Year;
        var month = axis == 1 ? (int)coordinate : current.Month;
        var day = Math.Min(axis == 2 ? (int)coordinate : current.Day, DateTime.DaysInMonth(year, month));
        var hour = axis == 3 ? (int)coordinate : current.Hour;
        var minute = axis == 4 ? (int)coordinate : current.Minute;
        var ticks = axis == 5 ? checked((long)(coordinate * TimeSpan.TicksPerSecond)) : current.Ticks % TimeSpan.TicksPerMinute;
        return new DateTime(checked(new DateTime(year, month, day, hour, minute, 0).Ticks + ticks));
    }
    internal static bool Draw(EditorIcons icons, object owner, DateTime value, bool mixed, bool canEdit, float scale, out DateTime updated)
    {
        updated = value; var id = Gui.GetID("##DateTime"); var states = drafts.GetValue(owner, _ => new());
        if (!states.TryGetValue(id, out var draft)) states[id] = draft = new();
        var components = new double[] { value.Year, value.Month, value.Day, value.Hour, value.Minute, (double)(value.Ticks % TimeSpan.TicksPerMinute) / TimeSpan.TicksPerSecond };
        if (!draft.Initialized || draft.Value != value || draft.Mixed != mixed)
        {
            draft.Initialized = true; draft.Value = value; draft.Mixed = mixed;
            for (var axis = 0; axis < 6; axis++) draft.Text[axis] = mixed ? "" : components[axis].ToString(axis == 5 ? "F7" : "F0", CultureInfo.InvariantCulture);
        }
        Gui.BeginGroup(); Gui.BeginDisabled(mixed);
        var width = Gui.GetContentRegionAvail().X;
        Gui.SetNextItemWidth(Math.Max(1, width - 16 * scale - Gui.GetStyle().FramePadding.X * 2 - Gui.GetStyle().ItemSpacing.X));
        var summary = mixed ? "" : value.ToString(CultureInfo.CurrentCulture);
        Gui.InputTextWithHint("##DateTime", mixed ? "(Different values)" : "", ref summary, 512, ImGuiInputTextFlags.ReadOnly | ImGuiInputTextFlags.AutoSelectAll);
        Gui.SameLine(); Gui.BeginDisabled(!canEdit);
        if (icons.Button("pick-date-time", EditorLocalization.Text("Pick date and time", "ToolTip"), 16)) Gui.OpenPopup("##DateTimePopup");
        Gui.EndDisabled();
        var changed = false;
        Gui.SetNextWindowSizeConstraints(new System.Numerics.Vector2(Math.Max(1, width), 0), new System.Numerics.Vector2(float.MaxValue));
        if (Gui.BeginPopup("##DateTimePopup"))
        {
            try
            {
                if (!canEdit || mixed) Gui.CloseCurrentPopup();
                else
                {
                    var columnWidth = 1f;
                    for (var axis = 0; axis < 6; axis++)
                    {
                        if (axis % 3 != 0) Gui.SameLine();
                        Gui.PushID(axis);
                        if (axis % 3 == 0)
                        {
                            var row = axis < 3 ? 0 : 3;
                            var labels = Labels.Skip(row).Take(3).Sum(label => Gui.CalcTextSize(label).X);
                            columnWidth = Math.Max(1, (Gui.GetContentRegionAvail().X - labels - Gui.GetStyle().ItemSpacing.X * 5) / 3);
                        }
                        Gui.SetNextItemWidth(columnWidth);
                        var text = draft.Text[axis];
                        var submitted = Gui.InputText("##Coordinate", ref text, 256, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
                        var feedback = Gui.GetID("##Coordinate"); var finished = Gui.IsItemDeactivatedAfterEdit(); draft.Text[axis] = text;
                        try
                        {
                            var type = axis == 5 ? typeof(double) : typeof(int);
                            if (NativeNumericInput.StepText(type, components[axis], false, Ranges[axis], ref text))
                            { submitted = true; draft.Text[axis] = text; }
                            if (submitted || finished)
                            {
                                var coordinate = PropertyConversionFeedback.Convert(feedback, () => Convert.ToDouble(Ranges[axis].Normalize(double.Parse(text, CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture));
                                updated = ReplaceComponent(updated, axis, coordinate);
                                PropertyConversionFeedback.Validate(feedback); changed = true;
                            }
                        }
                        catch (Exception error) when (error is FormatException or OverflowException or ArgumentException)
                        { PropertyConversionFeedback.Fail(feedback, error); draft.Text[axis] = components[axis].ToString(axis == 5 ? "F7" : "F0", CultureInfo.InvariantCulture); }
                        PropertyConversionFeedback.Draw(feedback); Gui.PopID(); Gui.SameLine(); Gui.TextUnformatted(Labels[axis]);
                    }
                }
            }
            finally { Gui.EndPopup(); }
        }
        Gui.EndDisabled(); Gui.EndGroup(); return changed;
    }
}
