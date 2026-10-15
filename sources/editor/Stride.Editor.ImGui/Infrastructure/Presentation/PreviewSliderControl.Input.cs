using System.Runtime.CompilerServices;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace Stride.Editor.ImGui;

internal static partial class PreviewSliderControl
{
    private sealed class TrackPress
    {
        internal bool Repeating, Dragging;
        internal int Direction, LastFrame = -1;
        internal double Next, Interval;
        internal float PointerOffset;
        internal float Dpi;
    }
    private static readonly ConditionalWeakTable<object, Dictionary<uint, TrackPress>> trackPresses = new();
    private static readonly ImGuiKey[] SliderKeys = [ImGuiKey.UpArrow, ImGuiKey.DownArrow, ImGuiKey.LeftArrow,
        ImGuiKey.RightArrow, ImGuiKey.PageUp, ImGuiKey.PageDown, ImGuiKey.Home, ImGuiKey.End];
    internal static bool DrawFloat(object owner, string id, ref float value, float minimum, float maximum,
        float smallChange = .1f, float largeChange = 1, bool moveToPoint = true, float heightDips = 16, float thumbWidthDips = 10)
    {
        var before = value;
        var scale = Gui.GetStyle().FontScaleDpi;
        var size = new Vector2(Math.Max(1, Gui.CalcItemWidth()), Math.Max(1, heightDips * scale));
        // Native Button provides the real stable item/focus/capture boundary;
        // native Slider/RepeatButton semantics below determine the value before
        // its track/thumb is drawn, with no temporary numeric-input surface.
        Gui.PushStyleColor(ImGuiCol.Button, Vector4.Zero); Gui.PushStyleColor(ImGuiCol.ButtonHovered, Vector4.Zero);
        Gui.PushStyleColor(ImGuiCol.ButtonActive, Vector4.Zero);
        try { Gui.Button(id, size); }
        finally { Gui.PopStyleColor(3); }
        var identity = Gui.GetID(id); var io = Gui.GetIO();
        var enabled = (ImGuiP.GetItemFlags() & (ImGuiItemFlags)ImGuiItemFlagsPrivate.Disabled) == 0;
        var presses = trackPresses.GetOrCreateValue(owner);
        if (!presses.TryGetValue(identity, out var press)) presses.Add(identity, press = new());
        var frame = Gui.GetFrameCount();
        if (press.LastFrame != frame - 1 || press.Dpi != scale || !enabled || !Gui.IsItemActive() || !Gui.IsMouseDown(ImGuiMouseButton.Left))
            press.Repeating = press.Dragging = false;
        press.LastFrame = frame;
        press.Dpi = scale;
        var low = Gui.GetItemRectMin(); var high = Gui.GetItemRectMax();
        var grab = Math.Min(thumbWidthDips * scale, Math.Max(0, high.X - low.X));
        var start = low.X + grab * .5f; var extent = Math.Max(0, high.X - low.X - grab);
        float Center(float number) => start + (maximum > minimum ? Math.Clamp((number - minimum) / (maximum - minimum), 0, 1) * extent : 0);
        var center = Center(before);
        if (enabled && Gui.IsItemActivated() && Gui.IsMouseDown(ImGuiMouseButton.Left))
        {
            var onThumb = Math.Abs(io.MousePos.X - center) <= grab * .5f;
            if (moveToPoint || onThumb)
            { press.Dragging = true; press.PointerOffset = onThumb ? io.MousePos.X - center : 0; }
            else
            {
                var timing = WindowsRepeatInputTiming.Read();
                press.Repeating = true; press.Direction = io.MousePos.X > center ? 1 : -1;
                press.Next = Gui.GetTime() + timing.Delay; press.Interval = timing.Interval;
                value = Math.Clamp(before + largeChange * press.Direction, minimum, maximum);
            }
        }
        else if (press.Repeating)
        {
            var onSide = Gui.IsItemHovered() && (press.Direction > 0 ? io.MousePos.X > center + grab * .5f : io.MousePos.X < center - grab * .5f);
            if (onSide && Gui.GetTime() >= press.Next)
            { value = Math.Clamp(before + largeChange * press.Direction, minimum, maximum); press.Next = Gui.GetTime() + press.Interval; }
        }
        if (press.Dragging && extent > 0)
            value = minimum + Math.Clamp((io.MousePos.X - press.PointerOffset - start) / extent, 0, 1) * (maximum - minimum);
        if (enabled && (Gui.IsItemFocused() || Gui.IsItemActive()) && !Gui.IsMouseDown(ImGuiMouseButton.Left))
        {
            foreach (var key in SliderKeys) ImGuiP.SetKeyOwner(key, identity);
            if (!io.KeyCtrl && !io.KeyAlt && !io.KeyShift && !io.KeySuper)
            {
                float? next = Gui.IsKeyPressed(ImGuiKey.Home) ? minimum : Gui.IsKeyPressed(ImGuiKey.End) ? maximum
                    : Gui.IsKeyPressed(ImGuiKey.PageUp) ? before + largeChange : Gui.IsKeyPressed(ImGuiKey.PageDown) ? before - largeChange
                    : Gui.IsKeyPressed(ImGuiKey.UpArrow) || Gui.IsKeyPressed(ImGuiKey.RightArrow) ? before + smallChange
                    : Gui.IsKeyPressed(ImGuiKey.DownArrow) || Gui.IsKeyPressed(ImGuiKey.LeftArrow) ? before - smallChange : null;
                if (next is { } number) value = Math.Clamp(number, minimum, maximum);
            }
        }
        var draw = Gui.GetWindowDrawList(); var y = (low.Y + high.Y) * .5f;
        var trackLow = new Vector2(low.X, y - 2 * scale); var trackHigh = new Vector2(high.X, y + 2 * scale);
        draw.AddRectFilled(trackLow, trackHigh, Gui.GetColorU32(ImGuiCol.FrameBg), 2 * scale);
        draw.AddRect(trackLow, trackHigh, Gui.GetColorU32(ImGuiCol.Border), 2 * scale, ImDrawFlags.None, scale);
        center = Center(value);
        draw.AddRectFilled(new Vector2(center - grab * .5f, low.Y), new Vector2(center + grab * .5f, high.Y),
            Gui.GetColorU32(Gui.IsItemActive() ? ImGuiCol.SliderGrabActive : ImGuiCol.SliderGrab), 2 * scale);
        return !value.Equals(before);
    }
    internal static bool DrawInt(object owner, string id, ref int value, int minimum, int maximum)
    {
        var number = (float)value;
        if (!DrawFloat(owner, id, ref number, minimum, maximum, 1, 1)) return false;
        var updated = Math.Clamp((int)Math.Round(number, MidpointRounding.ToEven), minimum, maximum);
        if (updated == value) return false;
        value = updated; return true;
    }
}
