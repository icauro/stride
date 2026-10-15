using System.Globalization;
using System.Runtime.CompilerServices;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal static class NativeCharacterInput
{
    private sealed class Draft { internal char Value; internal bool Initialized, Mixed; internal string Glyph = "", Unicode = ""; }
    private static readonly ConditionalWeakTable<object, Dictionary<uint, Draft>> drafts = new();
    private static readonly PropertyNumericRange unicodeRange = new(new PropertyNumericMetadata(0, double.MaxValue, 1, 10, 0), typeof(double));
    internal static unsafe bool DrawGlyph(object owner, char value, bool mixed, bool canEdit, float width, out char updated)
    {
        updated = value; var states = drafts.GetValue(owner, _ => new()); var id = Gui.GetID("##Character");
        if (!states.TryGetValue(id, out var state)) states[id] = state = new();
        if (!state.Initialized || state.Value != value || state.Mixed != mixed)
        { state.Value = value; state.Mixed = mixed; state.Initialized = true; state.Glyph = mixed || value == '\0' ? "" : value.ToString(); }
        var previous = state.Glyph;
        var padding = Gui.GetStyle().FramePadding;
        Gui.PushStyleVar(ImGuiStyleVar.FramePadding, new System.Numerics.Vector2(Math.Max(padding.X,
            (width - Gui.CalcTextSize(state.Glyph.Length == 0 ? "M" : state.Glyph).X) * .5f), padding.Y));
        bool changed;
        try { changed = NativeStringInput.Draw("##Character", ref state.Glyph, canEdit ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.ReadOnly, selectAllOnFocus: false); }
        finally { Gui.PopStyleVar(); }
        if (!changed || !canEdit) return false;
        var prefix = 0; while (prefix < previous.Length && prefix < state.Glyph.Length && previous[prefix] == state.Glyph[prefix]) prefix++;
        var suffix = 0; while (suffix < previous.Length - prefix && suffix < state.Glyph.Length - prefix && previous[^(suffix + 1)] == state.Glyph[^(suffix + 1)]) suffix++;
        var insertedEnd = state.Glyph.Length - suffix;
        updated = insertedEnd > prefix ? state.Glyph[insertedEnd - 1] : state.Glyph.Length == 0 ? '\0' : state.Glyph[0];
        state.Glyph = updated == '\0' ? "" : updated.ToString();
        var input = ImGuiP.GetInputTextState(id);
        if (input.Handle != null) ImGuiP.ReloadUserBufAndKeepSelection(input);
        return true;
    }
    internal static unsafe void ResetGlyph(object owner)
    {
        var id = Gui.GetID("##Character");
        if (drafts.TryGetValue(owner, out var states) && states.TryGetValue(id, out var state)) state.Initialized = false;
        var input = ImGuiP.GetInputTextState(id);
        if (input.Handle != null) ImGuiP.ReloadUserBufAndKeepSelection(input);
    }
    internal static unsafe bool Draw(object owner, char value, bool mixed, bool canEdit, float scale, out char updated, Action<string>? capture = null)
    {
        updated = value; var changed = false;
        var states = drafts.GetValue(owner, _ => new()); var id = Gui.GetID("##Character");
        if (!states.TryGetValue(id, out var state)) states[id] = state = new();
        if (!state.Initialized || state.Value != value || state.Mixed != mixed)
        { state.Value = value; state.Mixed = mixed; state.Initialized = true; state.Glyph = mixed || value == '\0' ? "" : value.ToString(); state.Unicode = mixed ? "" : ((int)value).ToString(CultureInfo.InvariantCulture); }
        var available = Gui.GetContentRegionAvail().X;
        var width = Math.Min(40 * scale, Math.Max(1, available - Gui.GetStyle().ItemSpacing.X - 1));
        Gui.BeginGroup(); Gui.SetNextItemWidth(width); var previous = state.Glyph;
        var readOnly = canEdit ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.ReadOnly;
        var padding = Gui.GetStyle().FramePadding;
        Gui.PushStyleVar(ImGuiStyleVar.FramePadding, new System.Numerics.Vector2(Math.Max(padding.X,
            (width - Gui.CalcTextSize(state.Glyph.Length == 0 ? "M" : state.Glyph).X) * .5f), padding.Y));
        var glyphChanged = Gui.InputText("##Character", ref state.Glyph, 4096, readOnly);
        Gui.PopStyleVar();
        if (glyphChanged && canEdit)
        {
            var prefix = 0; while (prefix < previous.Length && prefix < state.Glyph.Length && previous[prefix] == state.Glyph[prefix]) prefix++;
            var suffix = 0; while (suffix < previous.Length - prefix && suffix < state.Glyph.Length - prefix && previous[^(suffix + 1)] == state.Glyph[^(suffix + 1)]) suffix++;
            var insertedEnd = state.Glyph.Length - suffix;
            updated = insertedEnd > prefix ? state.Glyph[insertedEnd - 1] : state.Glyph.Length == 0 ? '\0' : state.Glyph[0];
            state.Glyph = updated == '\0' ? "" : updated.ToString(); state.Unicode = ((int)updated).ToString(CultureInfo.InvariantCulture); changed = true;
            var input = ImGuiP.GetInputTextState(Gui.GetID("##Character"));
            if (input.Handle != null) ImGuiP.ReloadUserBufAndKeepSelection(input);
        }
        capture?.Invoke("glyph");
        if (Gui.IsItemHovered()) Gui.SetTooltip(EditorLocalization.Text("Character", "ToolTIp"));
        Gui.SameLine(); Gui.SetNextItemWidth(Math.Max(1, available - width - Gui.GetStyle().ItemSpacing.X));
        var submitted = Gui.InputTextWithHint("##Unicode", mixed ? "(Different values)" : "", ref state.Unicode, 512, ImGuiInputTextFlags.EnterReturnsTrue | readOnly);
        capture?.Invoke("unicode");
        var feedback = Gui.GetID("##Unicode"); var finished = Gui.IsItemDeactivatedAfterEdit();
        if (canEdit)
        {
            try
            {
                if (NativeNumericInput.TryGesture(typeof(double), unicodeRange, out var amount))
                {
                    var baseline = mixed ? (object?)null : (double)(int)(changed ? updated : value);
                    state.Unicode = Convert.ToString(NativeNumericInput.Step(typeof(double), baseline, unicodeRange, amount), CultureInfo.InvariantCulture) ?? ""; submitted = true;
                }
                if (submitted || finished)
                {
                    updated = PropertyConversionFeedback.Convert(feedback, () => Convert.ToChar(Convert.ToUInt32(Math.Max(0, double.Parse(state.Unicode, CultureInfo.InvariantCulture))), CultureInfo.InvariantCulture));
                    changed = true; PropertyConversionFeedback.Validate(feedback);
                }
            }
            catch (Exception error) when (error is FormatException or OverflowException or ArgumentException) { PropertyConversionFeedback.Fail(feedback, error); }
        }
        if (Gui.IsItemHovered()) Gui.SetTooltip(EditorLocalization.Text("Unicode value", "ToolTip"));
        PropertyConversionFeedback.Draw(feedback); Gui.EndGroup(); return changed;
    }
}
