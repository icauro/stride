using System.Numerics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

/// <summary>Literal captions for template data; these rows are outside multiselect scopes.</summary>
internal static class EditorLiteralTemplateControls
{
    internal static unsafe bool Checkbox(string identity, string caption, ref bool value)
    {
        var window = ImGuiP.GetCurrentWindow(); if (window.SkipItems) return false;
        var style = Gui.GetStyle(); var id = Gui.GetID(identity); var position = window.DC.CursorPos;
        var text = Gui.CalcTextSize(caption); var side = Gui.GetFrameHeight();
        var size = new Vector2(side + (caption.Length == 0 ? 0 : style.ItemInnerSpacing.X + text.X), Math.Max(side, text.Y + style.FramePadding.Y * 2));
        var bounds = new ImRect(position, position + size);
        ImGuiP.ItemSize(size, style.FramePadding.Y); if (!ImGuiP.ItemAdd(bounds, id)) return false;
        var hovered = false; var held = false;
        var activated = ImGuiP.ButtonBehavior(bounds, id, ref hovered, ref held);
        if (activated) { value = !value; ImGuiP.MarkItemEdited(id); }
        ImGuiP.RenderNavCursor(bounds, id);
        ImGuiP.RenderFrame(position, position + new Vector2(side), Gui.GetColorU32(held && hovered ? ImGuiCol.FrameBgActive : hovered ? ImGuiCol.FrameBgHovered : ImGuiCol.FrameBg), true, style.FrameRounding);
        if (value)
        {
            var pad = Math.Max(1, MathF.Truncate(side / 6));
            ImGuiP.RenderCheckMark(window.DrawList, position + new Vector2(pad), Gui.GetColorU32(ImGuiCol.CheckMark), side - pad * 2);
        }
        window.DrawList.PushClipRect(window.InnerClipRect.Min, window.InnerClipRect.Max, true);
        try { window.DrawList.AddText(position + new Vector2(side + style.ItemInnerSpacing.X, style.FramePadding.Y), Gui.GetColorU32(ImGuiCol.Text), caption); }
        finally { window.DrawList.PopClipRect(); }
        return activated;
    }

    internal static unsafe bool BeginCombo(string identity, string preview)
    {
        // Toolkit owns the real combo/popup. Its empty preview prevents its label
        // parser from truncating authored text; paint into the actual parent list.
        var window = ImGuiP.GetCurrentWindow(); var draw = window.DrawList;
        if (window.SkipItems) return Gui.BeginCombo(identity, "");
        var style = Gui.GetStyle(); var origin = window.DC.CursorPos;
        var width = Gui.CalcItemWidth(); var side = Gui.GetFrameHeight();
        var minimum = Vector2.Max(origin + style.FramePadding, window.InnerClipRect.Min);
        var maximum = Vector2.Min(origin + new Vector2(Math.Max(0, width - side), side), window.InnerClipRect.Max);
        var open = Gui.BeginCombo(identity, "");
        try
        {
            if (maximum.X > minimum.X && maximum.Y > minimum.Y)
            {
                draw.PushClipRect(minimum, maximum, true);
                try { draw.AddText(origin + style.FramePadding, Gui.GetColorU32(ImGuiCol.Text), preview); }
                finally { draw.PopClipRect(); }
            }
        }
        catch { if (open) Gui.EndCombo(); throw; }
        return open;
    }

    internal static bool Selectable(string identity, string caption, bool selected)
    {
        var position = Gui.GetCursorScreenPos(); var size = Gui.CalcTextSize(caption);
        var activated = Gui.Selectable(identity, selected, (ImGuiSelectableFlags)ImGuiSelectableFlagsPrivate.SpanAvailWidth, size);
        if (Gui.IsItemVisible())
        {
            var draw = Gui.GetWindowDrawList(); draw.PushClipRect(Gui.GetItemRectMin(), Gui.GetItemRectMax(), true);
            try { draw.AddText(position, Gui.GetColorU32(ImGuiCol.Text), caption); }
            finally { draw.PopClipRect(); }
        }
        return activated;
    }
}
