using System.Numerics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal static class EditorNativeTreeGeometry
{
    internal static ImGuiScopes.StyleScope RowPadding(float textHeight, ref ImGuiTreeNodeFlags flags)
    {
        var padding = Gui.GetStyle().FramePadding;
        var framed = (flags & (ImGuiTreeNodeFlags.FramePadding | ImGuiTreeNodeFlags.Framed)) != 0;
        var difference = Math.Max(0, textHeight - Gui.GetTextLineHeight());
        // TreeNodeBehavior and virtualization consume this same actual row metric.
        padding.Y = (framed ? padding.Y : 0) + difference / 2;
        flags |= ImGuiTreeNodeFlags.FramePadding;
        return ImGuiScopes.Style(ImGuiStyleVar.FramePadding, padding);
    }
}
