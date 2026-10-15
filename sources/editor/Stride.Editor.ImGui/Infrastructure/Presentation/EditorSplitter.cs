using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed class EditorSplitterState
{
    internal bool Dragging;
    internal float OriginalValue, OriginalLength, OriginalExtent, Dpi, MouseOrigin, AppliedDelta;
    internal int LastFrame = -1;
}

/// <summary>Managed layout values around the native focus/key ownership boundary.</summary>
internal static class EditorSplitter
{
    internal static bool Draw(string id, NVector2 size, bool column, bool relative, EditorSplitterState state,
        ref float value, float actualLengthDips, float extentDips, float minimum = 0, float maximum = float.PositiveInfinity)
    {
        var scale = Gui.GetStyle().FontScaleDpi; var frame = Gui.GetFrameCount(); var changed = false;
        if (state.Dragging && (state.LastFrame != frame - 1 || state.Dpi != scale || relative && Math.Abs(state.OriginalExtent - extentDips) > .01f))
        { value = state.OriginalValue; state.Dragging = false; changed = true; }
        state.LastFrame = frame;
        Gui.PushStyleVar(ImGuiStyleVar.FramePadding, NVector2.Zero); Gui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0);
        Gui.PushStyleColor(ImGuiCol.Button, Gui.GetStyle().Colors[(int)ImGuiCol.Separator]);
        Gui.PushStyleColor(ImGuiCol.ButtonHovered, Gui.GetStyle().Colors[(int)ImGuiCol.SeparatorHovered]);
        Gui.PushStyleColor(ImGuiCol.ButtonActive, Gui.GetStyle().Colors[(int)ImGuiCol.SeparatorActive]);
        try { Gui.Button(id, NVector2.Max(size, NVector2.One)); }
        finally { Gui.PopStyleColor(3); Gui.PopStyleVar(2); }
        var io = Gui.GetIO(); var active = Gui.IsItemActive(); var focused = Gui.IsItemFocused();
        if (Gui.IsItemHovered() || active) Gui.SetMouseCursor(column ? ImGuiMouseCursor.ResizeEw : ImGuiMouseCursor.ResizeNs);
        var mouse = column ? io.MousePos.X : io.MousePos.Y;
        if (Gui.IsItemActivated() && Gui.IsMouseDown(ImGuiMouseButton.Left))
        {
            state.Dragging = true; state.OriginalValue = value; state.OriginalLength = actualLengthDips;
            state.OriginalExtent = extentDips; state.Dpi = scale; state.MouseOrigin = mouse;
            state.AppliedDelta = 0;
        }
        if (focused || active)
        {
            var identity = Gui.GetID(id);
            // Keep navigation arrows with the splitter while focused, without
            // retaining any context/window pointer beyond this native call.
            ImGuiP.SetKeyOwner(ImGuiKey.LeftArrow, identity); ImGuiP.SetKeyOwner(ImGuiKey.RightArrow, identity);
            ImGuiP.SetKeyOwner(ImGuiKey.UpArrow, identity); ImGuiP.SetKeyOwner(ImGuiKey.DownArrow, identity);
            if (state.Dragging) ImGuiP.SetKeyOwner(ImGuiKey.Escape, identity);
        }
        if (state.Dragging)
        {
            if (io.AppFocusLost || !Gui.IsMousePosValid() || !focused && Gui.IsMouseDown(ImGuiMouseButton.Left) || Gui.IsKeyPressed(ImGuiKey.Escape))
            { value = state.OriginalValue; state.Dragging = false; return true; }
            if (Gui.IsMouseDown(ImGuiMouseButton.Left))
            {
                var delta = MathF.Round((mouse - state.MouseOrigin) / scale, MidpointRounding.ToEven);
                if (delta != state.AppliedDelta)
                {
                    var length = Math.Clamp(state.OriginalLength + delta, minimum, maximum);
                    value = relative ? extentDips > 0 ? length / extentDips : 0 : length;
                    state.AppliedDelta = delta; changed = true;
                }
            }
            else state.Dragging = false;
        }
        else if (focused && !io.WantTextInput)
        {
            var decrease = Gui.IsKeyPressed(column ? ImGuiKey.LeftArrow : ImGuiKey.UpArrow);
            var increase = Gui.IsKeyPressed(column ? ImGuiKey.RightArrow : ImGuiKey.DownArrow);
            var perpendicular = Gui.IsKeyPressed(column ? ImGuiKey.UpArrow : ImGuiKey.LeftArrow)
                || Gui.IsKeyPressed(column ? ImGuiKey.DownArrow : ImGuiKey.RightArrow);
            if (increase || decrease || perpendicular)
            {
                var length = Math.Clamp(actualLengthDips + (increase ? 10 : decrease ? -10 : 0), minimum, maximum);
                value = relative ? extentDips > 0 ? length / extentDips : 0 : length; changed = true;
            }
        }
        return changed;
    }
}
