using System.Numerics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private unsafe bool DrawNativeCheckbox(string label, ref bool value)
        => DrawNativeCheckbox(label, ref value, null, Vector2.Zero, 0);
    private unsafe bool DrawNativeCheckbox(string label, ref bool value, ImTextureRef? image, Vector2 imageSize, float imageMargin)
        => DrawNativeCheckboxCore(label, ref value, image, imageSize, imageMargin, null);
    private unsafe bool DrawNativeLiteralCheckbox(string identity, string text, ref bool value, ImTextureRef? image = null, Vector2 imageSize = default, float imageMargin = 0)
        => DrawNativeCheckboxCore(identity, ref value, image, imageSize, imageMargin, text);
    private unsafe bool DrawNativeCheckboxCore(string label, ref bool value, ImTextureRef? image, Vector2 imageSize, float imageMargin, string? literalText)
    {
        if (literalText == null && (!CanDrawNativeWidgetText || !Gui.GetCurrentContext().CurrentMultiSelect.IsNull)) return Gui.Checkbox(label, ref value);
        var window = ImGuiP.GetCurrentWindow();
        if (window.SkipItems) return false;
        var identity = Gui.GetID(label); var style = Gui.GetStyle();
        var visible = literalText ?? NativeVisibleWidgetLabel(label);
        var text = CanDrawNativeWidgetText ? NativeWidgetLayout(visible) : null;
        var textSize = text?.Size ?? Gui.CalcTextSize(visible);
        var side = Gui.GetFrameHeight(); var position = window.DC.CursorPos;
        var textHeight = visible.Length == 0 ? 0 : textSize.Y;
        var imageWidth = imageSize.X > 0 ? imageSize.X + imageMargin : 0;
        var contentHeight = Math.Max(textHeight, imageSize.Y);
        var size = new Vector2(side + (visible.Length == 0 && imageWidth == 0 ? 0 : style.ItemInnerSpacing.X + imageWidth + textSize.X), Math.Max(side, contentHeight + style.FramePadding.Y * 2));
        var bounds = new ImRect(position, position + size);
        ImGuiP.ItemSize(size, style.FramePadding.Y);
        if (!ImGuiP.ItemAdd(bounds, identity)) return false;
        var mixed = (Gui.GetCurrentContext().LastItemData.ItemFlags & (ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue) != 0;
        var hovered = false; var held = false;
        var pressed = ImGuiP.ButtonBehavior(bounds, identity, ref hovered, ref held);
        if (pressed) { value = !value; ImGuiP.MarkItemEdited(identity); }
        ImGuiP.RenderNavCursor(bounds, identity);
        var box = new ImRect(position, position + new Vector2(side));
        ImGuiP.RenderFrame(box.Min, box.Max, Gui.GetColorU32(held && hovered ? ImGuiCol.FrameBgActive : hovered ? ImGuiCol.FrameBgHovered : ImGuiCol.FrameBg), true, style.FrameRounding);
        if (mixed)
        {
            var pad = new Vector2(Math.Max(1, MathF.Truncate(side / 3.6f)));
            window.DrawList.AddRectFilled(box.Min + pad, box.Max - pad, Gui.GetColorU32(ImGuiCol.CheckMark), style.FrameRounding);
        }
        else if (value)
        {
            var pad = Math.Max(1, MathF.Truncate(side / 6));
            ImGuiP.RenderCheckMark(window.DrawList, box.Min + new Vector2(pad), Gui.GetColorU32(ImGuiCol.CheckMark), side - pad * 2);
        }
        var origin = position + new Vector2(side + style.ItemInnerSpacing.X,
            imageWidth > 0 ? (size.Y - textHeight) * .5f : style.FramePadding.Y);
        if (image is { } leadingImage)
        {
            var imageOrigin = position + new Vector2(side + style.ItemInnerSpacing.X, (size.Y - imageSize.Y) * .5f);
            window.DrawList.AddImage(leadingImage, imageOrigin, imageOrigin + imageSize);
        }
        origin.X += imageWidth;
        if (visible.Length != 0)
        {
            if (text != null) PaintNativeWidget(text, origin, window.InnerClipRect.Min, window.InnerClipRect.Max);
            else
            {
                window.DrawList.PushClipRect(window.InnerClipRect.Min, window.InnerClipRect.Max, true);
                try { window.DrawList.AddText(origin, Gui.GetColorU32(ImGuiCol.Text), visible); }
                finally { window.DrawList.PopClipRect(); }
            }
        }
        if (Gui.GetCurrentContext().LogEnabled) ImGuiP.LogSetNextTextDecoration(mixed ? "[~]" : value ? "[x]" : "[ ]", "");
        LogNativeWidgetText(visible, origin);
        return pressed;
    }
    private unsafe bool BeginNativeCombo(string label, string? preview, ImGuiComboFlags flags = ImGuiComboFlags.None)
    {
        // Native custom-preview state is left to the toolkit until that whole contract is ported.
        if (!CanDrawNativeWidgetText || (flags & (ImGuiComboFlags)ImGuiComboFlagsPrivate.CustomPreview) != 0)
            return Gui.BeginCombo(label, preview, flags);
        if ((flags & (ImGuiComboFlags.NoArrowButton | ImGuiComboFlags.NoPreview)) == (ImGuiComboFlags.NoArrowButton | ImGuiComboFlags.NoPreview)
            || (flags & ImGuiComboFlags.WidthFitPreview) != 0 && (flags & ImGuiComboFlags.NoPreview) != 0)
            return Gui.BeginCombo(label, preview, flags);
        var context = Gui.GetCurrentContext();
        var nextFlags = context.NextWindowData.HasFlags;
        context.NextWindowData.HasFlags = ImGuiNextWindowDataFlags.None;
        var window = ImGuiP.GetCurrentWindow();
        if (window.SkipItems) return false;
        var style = Gui.GetStyle(); var identity = Gui.GetID(label);
        var visible = NativeVisibleWidgetLabel(label); var text = NativeWidgetLayout(visible);
        var valueText = NativeWidgetLayout(preview ?? "");
        var arrow = (flags & ImGuiComboFlags.NoArrowButton) != 0 ? 0 : Gui.GetFrameHeight();
        var width = (flags & ImGuiComboFlags.NoPreview) != 0 ? arrow : (flags & ImGuiComboFlags.WidthFitPreview) != 0
            ? arrow + valueText.Width + style.FramePadding.X * 2 : Gui.CalcItemWidth();
        if (preview != null && (flags & (ImGuiComboFlags.NoPreview | ImGuiComboFlags.WidthFitPreview)) == 0)
        {
            var previewWidth = Math.Max(1, width - arrow - style.FramePadding.X * 2);
            if (valueText.Width > previewWidth) valueText = NativeWidgetLayout(preview, previewWidth, wrap: true);
        }
        var position = window.DC.CursorPos;
        var height = Math.Max(Gui.GetFrameHeight(), preview != null && (flags & ImGuiComboFlags.NoPreview) == 0 ? valueText.Height + style.FramePadding.Y * 2 : 0);
        var box = new ImRect(position, position + new Vector2(width, height));
        var total = new ImRect(box.Min, new Vector2(box.Max.X + (visible.Length == 0 ? 0 : style.ItemInnerSpacing.X + text.Width),
            Math.Max(box.Max.Y, position.Y + (visible.Length == 0 ? 0 : text.Height + style.FramePadding.Y * 2))));
        ImGuiP.ItemSize(total, style.FramePadding.Y);
        if (!ImGuiP.ItemAdd(total, identity, ref box)) return false;
        var hovered = false; var held = false;
        var pressed = ImGuiP.ButtonBehavior(box, identity, ref hovered, ref held);
        var popup = ImGuiP.ImHashStr("##ComboPopup", (nuint)0, identity);
        var open = ImGuiP.IsPopupOpen(popup, ImGuiPopupFlags.None);
        if (pressed && !open) { ImGuiP.OpenPopupEx(popup, ImGuiPopupFlags.None); open = true; }
        var split = Math.Max(box.Min.X, box.Max.X - arrow);
        ImGuiP.RenderNavCursor(box, identity);
        if ((flags & ImGuiComboFlags.NoPreview) == 0)
            window.DrawList.AddRectFilled(box.Min, new(split, box.Max.Y), Gui.GetColorU32(hovered ? ImGuiCol.FrameBgHovered : ImGuiCol.FrameBg), style.FrameRounding,
                arrow == 0 ? ImDrawFlags.RoundCornersAll : ImDrawFlags.RoundCornersLeft);
        if (arrow != 0)
        {
            window.DrawList.AddRectFilled(new(split, box.Min.Y), box.Max, Gui.GetColorU32(open || hovered ? ImGuiCol.ButtonHovered : ImGuiCol.Button), style.FrameRounding,
                width <= arrow ? ImDrawFlags.RoundCornersAll : ImDrawFlags.RoundCornersRight);
            if (split + arrow - style.FramePadding.X <= box.Max.X)
                ImGuiP.RenderArrow(window.DrawList, new(split + style.FramePadding.Y, box.Min.Y + style.FramePadding.Y), Gui.GetColorU32(ImGuiCol.Text), ImGuiDir.Down);
        }
        ImGuiP.RenderFrameBorder(box.Min, box.Max, style.FrameRounding);
        if (preview != null && (flags & ImGuiComboFlags.NoPreview) == 0)
        {
            var origin = box.Min + style.FramePadding;
            PaintNativeWidget(valueText, origin, origin, new(split, box.Max.Y));
            if (context.LogEnabled) ImGuiP.LogSetNextTextDecoration("{", "}");
            LogNativeWidgetText(preview, origin);
        }
        if (visible.Length != 0)
        {
            var origin = new Vector2(box.Max.X + style.ItemInnerSpacing.X, box.Min.Y + style.FramePadding.Y);
            PaintNativeWidget(text, origin, window.InnerClipRect.Min, window.InnerClipRect.Max);
            LogNativeWidgetText(visible, origin);
        }
        if (!open) return false;
        context.NextWindowData.HasFlags = nextFlags;
        if ((nextFlags & ImGuiNextWindowDataFlags.HasSizeConstraint) == 0)
        {
            var count = (flags & ImGuiComboFlags.HeightSmall) != 0 ? 4 : (flags & ImGuiComboFlags.HeightLarge) != 0 ? 20
                : (flags & ImGuiComboFlags.HeightLargest) != 0 ? -1 : 8;
            var explicitHeight = (nextFlags & ImGuiNextWindowDataFlags.HasSize) != 0 && context.NextWindowData.SizeVal.Y > 0;
            if (!explicitHeight && count > 0)
            {
                var rowHeight = Math.Max(Gui.GetFontSize(), valueText.Height);
                Gui.SetNextWindowSizeConstraints(Vector2.Zero, new(float.MaxValue, (rowHeight + style.ItemSpacing.Y) * count - style.ItemSpacing.Y + style.WindowPadding.Y * 2));
            }
        }
        return ImGuiP.BeginComboPopup(popup, box, flags);
    }
}
