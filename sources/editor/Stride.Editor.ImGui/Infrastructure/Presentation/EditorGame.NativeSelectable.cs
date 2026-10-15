using System.Numerics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool DrawNativeSelectable(string label, bool selected = false, ImGuiSelectableFlags flags = ImGuiSelectableFlags.None, Vector2 size = default)
        => DrawNativeSelectableCore(label, selected, flags, size, null);
    private bool DrawNativeLiteralSelectable(string identity, string text, bool selected = false, ImGuiSelectableFlags flags = ImGuiSelectableFlags.None, Vector2 size = default)
        => DrawNativeSelectableCore(identity, selected, flags, size, text);
    private bool DrawNativeSelectableCore(string label, bool selected, ImGuiSelectableFlags flags, Vector2 size, string? literalText)
    {
        if (literalText == null && (!CanDrawNativeWidgetText || size.X < 0 || size.Y < 0)) return Gui.Selectable(label, selected, flags, size);
        var window = ImGuiP.GetCurrentWindow();
        if (window.SkipItems) return false;
        var visible = literalText ?? NativeVisibleWidgetLabel(label); var identity = Gui.GetID(label);
        var disabled = (flags & ImGuiSelectableFlags.Disabled) != 0 && (Gui.GetCurrentContext().CurrentItemFlags & (ImGuiItemFlags)ImGuiItemFlagsPrivate.Disabled) == 0;
        var color = Gui.GetColorU32(ImGuiCol.Text, disabled ? Gui.GetStyle().DisabledAlpha : 1);
        var text = CanDrawNativeWidgetText ? NativeWidgetLayout(visible, color: color) : null;
        var textSize = text?.Size ?? Gui.CalcTextSize(visible);
        var origin = window.DC.CursorPos + new Vector2(0, window.DC.CurrLineTextBaseOffset);
        if (size.X == 0)
        {
            size.X = textSize.X;
            flags |= (ImGuiSelectableFlags)ImGuiSelectableFlagsPrivate.SpanAvailWidth;
        }
        if (size.Y == 0) size.Y = textSize.Y;
        // Empty-label hashing returns the overridden seed. Native Selectable owns the whole interaction.
        ImGuiP.PushOverrideID(identity);
        bool activated;
        try { activated = Gui.Selectable("", selected, flags, size); }
        finally { Gui.PopID(); }
        if (Gui.IsItemVisible())
        {
            var maximum = Gui.GetItemRectMax();
            if (text != null) PaintNativeWidget(text, origin, Vector2.Max(origin, window.InnerClipRect.Min), Vector2.Min(maximum, window.InnerClipRect.Max));
            else
            {
                window.DrawList.PushClipRect(Vector2.Max(origin, window.InnerClipRect.Min), Vector2.Min(maximum, window.InnerClipRect.Max), true);
                try { window.DrawList.AddText(origin, color, visible); }
                finally { window.DrawList.PopClipRect(); }
            }
            LogNativeWidgetText(visible, origin);
        }
        return activated;
    }
}
