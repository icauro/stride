using System.Globalization;
using System.Runtime.CompilerServices;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

/// <summary>The visible-spin NumericTextBox used by the native animation preview.</summary>
internal static class PreviewNumericControl
{
    private sealed class State
    {
        internal string Text = "";
        internal float Baseline;
        internal bool Initialized, Editing, Repeating;
        internal int LastFrame = -1, Direction;
        internal double Next, Interval;
    }
    private static readonly ConditionalWeakTable<object, Dictionary<uint, State>> states = new();
    private static readonly PropertyNumericRange rateRange = new(new PropertyNumericMetadata(0, null, .1, 1, 3), typeof(float));

    internal static bool DrawTimeScale(object owner, string id, ref float value)
    {
        var enabled = (ImGuiP.GetItemFlags() & (ImGuiItemFlags)ImGuiItemFlagsPrivate.Disabled) == 0;
        var identity = Gui.GetID(id);
        var values = states.GetOrCreateValue(owner);
        if (!values.TryGetValue(identity, out var state)) values.Add(identity, state = new());
        var frame = Gui.GetFrameCount();
        if (!state.Initialized || state.LastFrame != frame - 1 || !enabled || state.Baseline != value)
        { state.Editing = state.Repeating = false; state.Text = Format(value); }
        state.Initialized = true; state.LastFrame = frame; state.Baseline = value;
        var before = value;
        var width = Gui.CalcItemWidth(); var height = Gui.GetFrameHeight();
        var scale = Gui.GetStyle().FontScaleDpi;
        var spinWidth = Math.Min(16 * scale, width * .4f);
        Gui.BeginGroup(); Gui.PushID(id);
        try
        {
            Gui.SetNextItemWidth(Math.Max(1, width - spinWidth));
            var submit = NativeStringInput.Draw("##Text", ref state.Text, ImGuiInputTextFlags.EnterReturnsTrue);
            var active = Gui.IsItemActive(); var lostFocus = Gui.IsItemDeactivated();
            if (active) state.Editing = true;
            var canceled = active && Gui.IsKeyPressed(ImGuiKey.Escape);
            if (enabled && NativeNumericInput.TryGesture(typeof(float), rateRange, out var amount))
            {
                value = (float)NativeNumericInput.Step(typeof(float), value, rateRange, amount);
                state.Text = Format(value); state.Editing = false;
            }
            else if (canceled)
            { state.Text = Format(value); state.Editing = false; }
            else if (enabled && state.Editing && (submit || lostFocus))
            {
                if (TryParse(state.Text, out var number) && double.IsFinite(number))
                    value = (float)Math.Round(Math.Clamp(number, 0, float.MaxValue), 3);
                state.Text = Format(value); state.Editing = false;
            }
            Gui.SameLine(0, 0);
            var start = Gui.GetCursorScreenPos();
            for (var button = 0; button < 2; ++button)
            {
                var direction = button == 0 ? 1 : -1;
                Gui.SetCursorScreenPos(start + new Vector2(0, button * height * .5f));
                Gui.Button(button == 0 ? "##Increase" : "##Decrease", new Vector2(spinWidth, height * .5f));
                var pressed = enabled && Gui.IsItemActivated();
                if (pressed)
                {
                    var timing = WindowsRepeatInputTiming.Read();
                    state.Repeating = true; state.Direction = direction;
                    state.Next = Gui.GetTime() + timing.Delay; state.Interval = timing.Interval;
                }
                var repeat = enabled && state.Repeating && state.Direction == direction && Gui.IsItemActive()
                    && Gui.IsItemHovered() && Gui.IsMouseDown(ImGuiMouseButton.Left) && Gui.GetTime() >= state.Next;
                if (pressed || repeat)
                {
                    value = (float)NativeNumericInput.Step(typeof(float), value, rateRange, .1 * direction);
                    state.Text = Format(value); state.Editing = false;
                    if (repeat) state.Next = Gui.GetTime() + state.Interval;
                }
                var center = (Gui.GetItemRectMin() + Gui.GetItemRectMax()) * .5f;
                var radius = Math.Min(3 * scale, height * .2f);
                Gui.GetWindowDrawList().AddTriangleFilled(center + new Vector2(-radius, direction * radius * .5f),
                    center + new Vector2(radius, direction * radius * .5f),
                    center + new Vector2(0, -direction * radius * .5f), Gui.GetColorU32(ImGuiCol.Text));
            }
            if (!Gui.IsMouseDown(ImGuiMouseButton.Left)) state.Repeating = false;
            Gui.SetCursorScreenPos(start + new Vector2(spinWidth, height));
        }
        finally { Gui.PopID(); Gui.EndGroup(); }
        state.Baseline = value;
        return !value.Equals(before);
    }

    private static string Format(float value) => Math.Round(value, 3).ToString(CultureInfo.InvariantCulture);
    private static bool TryParse(string text, out double value)
    {
        const NumberStyles style = NumberStyles.Any & ~NumberStyles.AllowThousands;
        var prefix = text.AsSpan().TrimStart('0');
        if (prefix.StartsWith("x", StringComparison.OrdinalIgnoreCase) || prefix.StartsWith("#", StringComparison.OrdinalIgnoreCase))
        {
            // Floating-point TryParse rejects HexNumber; convert the actual
            // integer token before applying the preview's floating-point range.
            var parsed = ulong.TryParse(prefix.TrimStart(stackalloc char[] { 'x', '#' }), NumberStyles.HexNumber,
                NumberFormatInfo.InvariantInfo, out var hexadecimal);
            value = hexadecimal; return parsed;
        }
        return double.TryParse(text, style, CultureInfo.CurrentCulture, out value)
            || double.TryParse(text, style, CultureInfo.InvariantCulture, out value)
            || double.TryParse(text, style, CultureInfo.GetCultureInfo("de-DE"), out value);
    }
}
