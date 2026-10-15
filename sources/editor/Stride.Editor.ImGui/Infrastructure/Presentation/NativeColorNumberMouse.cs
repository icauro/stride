using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

/// <summary>Original NumericTextBox repeat timing and unfocused, oriented three-DIP mouse scrub.</summary>
internal static class NativeColorNumberMouse
{
    private sealed class State
    {
        internal bool Starting, Dragging, Repeating, Horizontal;
        internal int Direction, Frame = -1;
        internal double Number, Remainder, NextRepeat, Interval;
        internal Point Anchor;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { internal int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    private static readonly ConditionalWeakTable<object, Dictionary<uint, State>> states = new();
    internal static void DrawAdorner(object owner, uint identity, EditorIcons? icons, float scale)
    {
        if (!states.TryGetValue(owner, out var views) || !views.TryGetValue(identity, out var state) || !state.Dragging) return;
        Gui.SetMouseCursor(ImGuiMouseCursor.None);
        if (icons == null) return;
        var name = state.Horizontal ? "property-drag-horizontal" : "property-drag-vertical";
        var size = icons.Size(name) * scale;
        var minimum = Gui.GetItemRectMin(); var extent = Gui.GetItemRectMax() - minimum;
        var position = minimum + new Vector2(MathF.Round(extent.X - size.X), MathF.Round((extent.Y - size.Y) * .5f));
        Gui.GetWindowDrawList().AddImage(icons.Get(name), position, position + size, Vector2.Zero, Vector2.One, 0xffffffff);
    }

    internal static void Draw(object owner, uint identity, Func<double> read, PropertyNumericRange range, bool editable,
        bool textWasActive, float scale, Vector2 textStart, float textWidth, float height, Func<bool> begin,
        out string? text, out bool active, out bool commit)
    {
        var views = states.GetValue(owner, _ => new());
        if (!views.TryGetValue(identity, out var state)) views.Add(identity, state = new());
        text = null; active = commit = false;
        var frame = Gui.GetFrameCount();
        if (state.Frame != frame - 1 || !editable) state.Starting = state.Dragging = state.Repeating = false;
        state.Frame = frame;
        var mouse = Gui.GetIO().MousePos;
        if (editable && !textWasActive && Gui.IsMouseClicked(ImGuiMouseButton.Left)
            && mouse.X >= textStart.X && mouse.X < textStart.X + textWidth && mouse.Y >= textStart.Y && mouse.Y < textStart.Y + height && begin())
        { state.Starting = true; state.Number = read(); GetCursorPos(out state.Anchor); state.Remainder = 0; }
        if (state.Starting && Gui.IsMouseDown(ImGuiMouseButton.Left))
        {
            GetCursorPos(out var point); var delta = new Vector2(point.X - state.Anchor.X, point.Y - state.Anchor.Y);
            if (WindowsSourceTextDragDrop.ExceedsThreshold(delta))
            { state.Starting = false; state.Dragging = true; state.Horizontal = Math.Abs(delta.X) >= Math.Abs(delta.Y); state.Anchor = point; }
        }
        if (state.Dragging && Gui.IsMouseDown(ImGuiMouseButton.Left))
        {
            GetCursorPos(out var point);
            var distance = state.Horizontal ? point.X - state.Anchor.X : state.Anchor.Y - point.Y;
            if (distance != 0)
            {
                state.Remainder += distance / scale;
                var steps = Math.Floor(state.Remainder / 3); state.Remainder -= steps;
                state.Number = Convert.ToDouble(range.Normalize(state.Number + steps), CultureInfo.InvariantCulture);
            }
            SetCursorPos(state.Anchor.X, state.Anchor.Y); Gui.SetMouseCursor(ImGuiMouseCursor.None);
            active = true; text = state.Number.ToString(CultureInfo.InvariantCulture);
        }
        if (!Gui.IsMouseDown(ImGuiMouseButton.Left))
        {
            if (state.Dragging || state.Repeating) { text = state.Number.ToString(CultureInfo.InvariantCulture); commit = true; }
            state.Starting = state.Dragging = state.Repeating = false;
        }
        var cursor = Gui.GetCursorScreenPos(); var spinWidth = 16 * scale;
        Gui.PushID((int)identity);
        try
        {
            using var disabled = ImGuiScopes.Disabled(!editable);
            for (var button = 0; button < 2; button++)
            {
                var direction = button == 0 ? 1 : -1;
                var position = textStart + new Vector2(textWidth, height * button * .5f);
                Gui.SetCursorScreenPos(position);
                Gui.InvisibleButton(button == 0 ? "##Up" : "##Down", new Vector2(spinWidth, height * .5f));
                var pressed = editable && Gui.IsItemActivated() && begin();
                if (pressed)
                {
                    var timing = WindowsRepeatInputTiming.Read(); state.Number = read(); state.Repeating = true;
                    state.Direction = direction; state.NextRepeat = Gui.GetTime() + timing.Delay; state.Interval = timing.Interval;
                }
                var repeat = state.Repeating && state.Direction == direction && Gui.IsItemActive() && Gui.IsItemHovered()
                    && Gui.IsMouseDown(ImGuiMouseButton.Left) && Gui.GetTime() >= state.NextRepeat;
                if (pressed || repeat)
                {
                    state.Number = Convert.ToDouble(range.Normalize(state.Number + direction), CultureInfo.InvariantCulture);
                    if (repeat) state.NextRepeat = Gui.GetTime() + state.Interval;
                }
                if (state.Repeating && state.Direction == direction)
                { active = true; text = state.Number.ToString(CultureInfo.InvariantCulture); }
                var center = position + new Vector2(spinWidth, height * .5f) * .5f; var radius = 3 * scale;
                Gui.GetWindowDrawList().AddTriangleFilled(center + new Vector2(-radius, direction * radius * .5f),
                    center + new Vector2(radius, direction * radius * .5f), center + new Vector2(0, -direction * radius * .5f), Gui.GetColorU32(ImGuiCol.Text));
            }
        }
        finally { Gui.PopID(); Gui.SetCursorScreenPos(cursor); }
    }
}
