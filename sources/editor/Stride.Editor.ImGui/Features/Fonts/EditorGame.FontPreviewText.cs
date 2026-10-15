using System.Diagnostics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private string validatedFontPreviewText = "";
    private long? fontPreviewTextDeadline;
    private SpriteFontAssetPreview? fontPreviewTextOwner;
    private string? measuredFontPreviewText;
    private (float Maximum, float Line, float Padding, float Minimum) fontPreviewTextMetrics;
    private float measuredFontPreviewTextHeight;

    private void BindFontPreviewText(SpriteFontAssetPreview preview)
    {
        if (ReferenceEquals(fontPreviewTextOwner, preview)) return;
        preview.Sample = validatedFontPreviewText;
        fontPreviewTextOwner = preview;
    }
    private void ValidateFontPreviewText(SpriteFontAssetPreview preview, string text)
    {
        // This is preview text, not authored asset data. The same actor owns
        // the actual font and timer; no delayed callback retains a retired view.
        if (!ReferenceEquals(fontPreview, preview) || preview.IsDisposed) return;
        preview.Sample = text;
        validatedFontPreviewText = text;
        fontPreviewTextDeadline = null;
    }
    private void PumpFontPreviewText()
    {
        if (fontPreview is not { IsDisposed: false } preview)
        { fontPreviewTextOwner = null; fontPreviewTextDeadline = null; return; }
        BindFontPreviewText(preview);
        if (fontPreviewTextDeadline is { } deadline && Stopwatch.GetTimestamp() >= deadline)
            ValidateFontPreviewText(preview, fontPreviewText);
    }
    private void DrawFontPreviewText(SpriteFontAssetPreview preview, Vector2 size)
    {
        BindFontPreviewText(preview);
        var before = fontPreviewText;
        NativeStringInput.DrawMultiline("##FontPreviewText", ref fontPreviewText, size);
        Controls.Record("font-preview/text");
        var edited = Gui.IsItemEdited(); var active = Gui.IsItemActive();
        var canceled = (active || Gui.IsItemDeactivated()) && Gui.IsKeyPressed(ImGuiKey.Escape);
        if (canceled)
        { fontPreviewText = validatedFontPreviewText; fontPreviewTextDeadline = null; }
        else
        {
            if (active && (Gui.IsKeyPressed(ImGuiKey.Enter) || Gui.IsKeyPressed(ImGuiKey.KeypadEnter)))
                ValidateFontPreviewText(preview, before); // Native PreviewKeyDown precedes insertion of the return.
            if (edited && before != fontPreviewText)
                fontPreviewTextDeadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2;
            if (Gui.IsItemDeactivated()) ValidateFontPreviewText(preview, fontPreviewText);
        }
    }
    private void ResetFontPreviewText(bool clear)
    {
        fontPreviewTextOwner = null; fontPreviewTextDeadline = null;
        measuredFontPreviewText = null;
        if (clear) fontPreviewText = validatedFontPreviewText = "";
    }
    private float FontPreviewTextHeight()
    {
        var maximum = UiScale(32); var padding = Gui.GetStyle().FramePadding.Y * 2;
        var lineHeight = Gui.GetTextLineHeight();
        var minimum = Math.Max(Gui.GetFrameHeight(), UiScale(16) + padding);
        var metrics = (maximum, lineHeight, padding, minimum);
        if (ReferenceEquals(measuredFontPreviewText, fontPreviewText) && fontPreviewTextMetrics == metrics)
            return measuredFontPreviewTextHeight;
        var height = lineHeight + padding;
        // TextWrapping is not enabled in the original preview TextBox. Count
        // actual returns only, stopping once its native MaxHeight is reached.
        foreach (var character in fontPreviewText)
        {
            if (height >= maximum) break;
            if (character == '\n') height += lineHeight;
        }
        measuredFontPreviewText = fontPreviewText; fontPreviewTextMetrics = metrics;
        return measuredFontPreviewTextHeight = Math.Min(maximum, Math.Max(minimum, height));
    }
}
