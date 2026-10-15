using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace Stride.Editor.ImGui;

internal static class PropertyGridRows
{
    public static float AvailableWidth()
    {
        var available = Gui.GetContentRegionAvail().X;
        // An enclosing property table already constrains its cells. Reserve the
        // native window gutter exactly once, before the outermost row table.
        if (!Gui.GetCurrentContext().CurrentTable.IsNull) return Math.Max(1, available);
        var window = Hexa.NET.ImGui.ImGuiP.GetCurrentWindow();
        if ((window.Flags & ImGuiWindowFlags.NoScrollbar) != 0) return Math.Max(1, available);
        return Math.Max(1, available + window.ScrollbarSizes.X - Gui.GetStyle().ScrollbarSize);
    }
    public static float LabelWidth(float? available = null) => Math.Max(Gui.CalcTextSize("Rotation").X + Gui.GetStyle().ItemSpacing.X,
        (available ?? AvailableWidth()) * 0.4f);

    public static void DrawAxisBadge(int axis)
    {
        var style = Gui.GetStyle();
        var color = axis switch { 0 => new NVector4(0.85f, 0.3f, 0.3f, 1), 1 => new NVector4(0.35f, 0.7f, 0.35f, 1),
            2 => new NVector4(0.25f, 0.45f, 0.8f, 1), _ => new NVector4(0.5f, 0.5f, 0.5f, 1) };
        var label = "XYZW"[axis].ToString(); var position = Gui.GetCursorScreenPos();
        var size = new NVector2(Gui.CalcTextSize(label).X + style.FramePadding.X * 2, Gui.GetFrameHeight());
        var draw = Gui.GetWindowDrawList();
        draw.AddRectFilled(position, position + size, Gui.GetColorU32(color), style.FrameRounding);
        draw.AddText(position + style.FramePadding, Gui.GetColorU32(new NVector4(1, 1, 1, 1)), label);
        Gui.Dummy(size); Gui.SameLine(0, 0);
    }

    public static bool BeginRow(string label, bool drawLabel = true, Action<string>? labelRenderer = null)
    {
        var available = AvailableWidth();
        var labelWidth = LabelWidth(available);
        if (!Gui.BeginTable("##PropertyRow", 2, ImGuiTableFlags.SizingStretchProp, new NVector2(available, 0))) return false;
        Gui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthFixed, labelWidth, 0u);
        Gui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 1f, 0u);
        Gui.TableNextRow(); Gui.TableSetColumnIndex(0); Gui.AlignTextToFramePadding();
        if (drawLabel)
        {
            // The table's actual cell width is authoritative after padding and
            // scrolling. Wrapped native text contributes its full ItemSize,
            // so the row grows to the taller of the label and value controls.
            Gui.PushTextWrapPos(Gui.GetCursorPosX() + Math.Max(1, Gui.GetContentRegionAvail().X));
            try { if (labelRenderer != null) labelRenderer(label); else Gui.TextUnformatted(label); }
            finally { Gui.PopTextWrapPos(); }
        }
        Gui.TableSetColumnIndex(1); Gui.SetNextItemWidth(-1);
        return true;
    }
}
