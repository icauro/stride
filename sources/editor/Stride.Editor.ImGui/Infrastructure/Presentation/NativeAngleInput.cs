using System.Globalization;
using System.Runtime.CompilerServices;
using Stride.Core.Mathematics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal static class NativeAngleInput
{
    private sealed class Draft { internal float Observed; internal bool Initialized, Mixed; internal string Text = ""; }
    private static readonly ConditionalWeakTable<object, Dictionary<uint, Draft>> drafts = new();
    private static readonly PropertyNumericRange range = new(new PropertyNumericMetadata(DecimalPlaces: 3), typeof(double));
    internal static bool Draw(object owner, AngleSingle value, bool mixed, bool canEdit, out AngleSingle updated)
    {
        updated = value;
        var states = drafts.GetValue(owner, _ => new()); var id = Gui.GetID("##Angle");
        if (!states.TryGetValue(id, out var draft)) states[id] = draft = new();
        var degrees = Math.Round((double)value.Degrees, 3);
        if (!draft.Initialized || draft.Observed != value.Radians || draft.Mixed != mixed)
        { draft.Observed = value.Radians; draft.Mixed = mixed; draft.Initialized = true; draft.Text = mixed ? "" : degrees.ToString(CultureInfo.InvariantCulture); }
        var submitted = Gui.InputTextWithHint("##Angle", mixed ? "(Different values)" : "", ref draft.Text, 512,
            ImGuiInputTextFlags.EnterReturnsTrue | (canEdit ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.ReadOnly));
        var feedback = Gui.GetID("##Angle"); var finished = Gui.IsItemDeactivatedAfterEdit(); var changed = false;
        if (canEdit)
        {
            try
            {
                if (NativeNumericInput.StepText(typeof(double), degrees, mixed, range, ref draft.Text)) submitted = true;
                if (submitted || finished)
                {
                    updated = PropertyConversionFeedback.Convert(feedback, () => new AngleSingle((float)Math.Round(double.Parse(draft.Text, CultureInfo.InvariantCulture), 3), AngleType.Degree));
                    PropertyValueValidation.Normalize(updated, null); changed = true; PropertyConversionFeedback.Validate(feedback);
                }
            }
            catch (Exception error) when (error is FormatException or OverflowException or ArgumentException) { PropertyConversionFeedback.Fail(feedback, error); }
        }
        if (Gui.IsItemHovered()) Gui.SetTooltip(EditorLocalization.Text("Angle in degrees", "ToolTip"));
        PropertyConversionFeedback.Draw(feedback); return changed;
    }
}
