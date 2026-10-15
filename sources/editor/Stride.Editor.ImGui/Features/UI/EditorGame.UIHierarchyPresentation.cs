using System.Numerics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool DrawUIElementTreeNode(string label, string name, string type, string? library, int children, bool activeRoot,
        ImGuiTreeNodeFlags flags, bool forceSubmission, out bool virtualized)
    {
        var bold = PushPackagePropertiesBoldFont(activeRoot);
        try
        {
            var typeText = "[" + type + "]";
            var libraryText = library == null ? "" : "(Library: " + library + ")";
            var countText = children == 0 ? "" : "(" + children + ")";
            var nameSize = MeasureNativeLabel(name); var typeSize = MeasureNativeLabel(typeText);
            var librarySize = MeasureNativeLabel(libraryText); var countSize = MeasureNativeLabel(countText);
            var height = Math.Max(Math.Max(nameSize.Y, typeSize.Y), Math.Max(librarySize.Y, countSize.Y));
            using var padding = EditorNativeTreeGeometry.RowPadding(height, ref flags);
            virtualized = EditorTreeVirtualizationAdapter.TrySkipRow(label, flags, forceSubmission, out var expanded);
            if (virtualized) return expanded;
            expanded = ImGuiP.TreeNodeBehavior(Gui.GetID(label), flags, "");
            var minimum = Gui.GetItemRectMin(); var maximum = Gui.GetItemRectMax();
            var origin = minimum + new Vector2(Gui.GetTreeNodeToLabelSpacing(), Math.Max(0, (maximum.Y - minimum.Y - height) / 2));
            var countOrigin = new Vector2(Math.Max(origin.X, maximum.X - countSize.X - Gui.GetStyle().FramePadding.X), origin.Y);
            var textMaximum = children == 0 ? maximum : new Vector2(countOrigin.X - UiScale(4), maximum.Y);
            DrawNativeTreeText(name, origin, minimum, textMaximum);
            origin.X += nameSize.X + UiScale(4);
            DrawNativeTreeText(typeText, origin, minimum, textMaximum);
            origin.X += typeSize.X;
            if (library != null) DrawNativeTreeText(libraryText, origin, minimum, textMaximum,
                Gui.GetColorU32(new Vector4(176f / 255, 1, 211f / 255, 1)));
            if (children != 0) DrawNativeTreeText(countText, countOrigin, minimum, maximum);
            return expanded;
        }
        finally { if (bold) Gui.PopFont(); }
    }
}
