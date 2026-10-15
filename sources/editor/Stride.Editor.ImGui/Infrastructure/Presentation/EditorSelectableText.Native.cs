using System.Text;
using Hexa.NET.ImGui;
using Stride.Graphics;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed unsafe partial class EditorSelectableText
{
    internal bool UseNativeShaping { get; set; }
    internal WindowsGlyphImages? NativeGlyphImages { get; set; }
    internal CommandList? NativeGlyphCommands { get; set; }
    internal string NativeFontFamily { get; set; } = "Segoe UI";
    internal IReadOnlyList<WindowsTextLayout.ColorRange> NativeTextColors { get; set; } = Array.Empty<WindowsTextLayout.ColorRange>();
    private EditorReadonlyRichText? nativeTextView;
    private bool nativeLastDraw;
    private int[] nativeUtf8Offsets = [0];
    private uint nativeWidgetId;
    private string? nativeMappedSource;
    private EditorSelectableTextLink[] nativeLinkSource = [];
    private EditorSelectableTextHighlight[] nativeHighlightSource = [];
    private EditorRichTextLink[] nativeLinks = [];
    private EditorRichTextHighlight[] nativeHighlights = [];
    internal bool NativeHovered => nativeLastDraw && nativeTextView?.Hovered == true;
    internal bool NativeDoubleClicked => nativeLastDraw && nativeTextView?.OwnedMouseDoubleClicked == true;
    private void DrawNativeReadonly(string id, string text, bool wrap, Vector2? requestedSize, bool followTail,
        IReadOnlyList<EditorSelectableTextLink>? links, Action<string>? openLink, bool expandHeightToContent,
        IReadOnlyList<EditorSelectableTextHighlight>? highlights)
    {
        nativeTextView ??= new() { CopyWholeWhenEmpty = true, ShowSelectAllMenu = false };
        var identity = Gui.GetID(id); var replaced = nativeMappedSource != text || source != text;
        var appended = replaced && source.Length != 0 && text.StartsWith(source, StringComparison.Ordinal);
        if (identity != nativeWidgetId || replaced && !appended) { nativeWidgetId = identity; nativeTextView.Select(0, 0); }
        if (replaced || nativeUtf8Offsets.Length != text.Length + 1)
        {
            nativeMappedSource = text; source = text; original = Encoding.UTF8.GetBytes(text); nativeUtf8Offsets = new int[text.Length + 1];
            var bytes = 0;
            for (var index = 0; index < text.Length;)
            {
                var length = char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;
                for (var unit = 0; unit < length; unit++) nativeUtf8Offsets[index + unit] = bytes;
                bytes += Encoding.UTF8.GetByteCount(text.AsSpan(index, length)); index += length; nativeUtf8Offsets[index] = bytes;
            }
        }
        wrapWidth = -1;
        nativeTextView.GlyphImages = NativeGlyphImages; nativeTextView.GlyphCommands = NativeGlyphCommands; nativeTextView.FontFamily = NativeFontFamily;
        nativeTextView.FollowTailToleranceDips = FollowTailToleranceDips;
        nativeTextView.FollowTailWithSelection = FollowTailWithSelection;
        nativeTextView.TextColors = NativeTextColors;
        if (requestedSelection is { } requested)
        {
            nativeTextView.Select(NativeCharacterOffset(requested.Start), NativeCharacterOffset(requested.End), focusSelection);
            requestedSelection = null; focusSelection = revealSelection = false;
        }
        var effectiveLinks = openLink == null ? null : links;
        if (replaced || !SameNativeRanges(nativeLinkSource, effectiveLinks))
        {
            nativeLinkSource = effectiveLinks?.ToArray() ?? [];
            nativeLinks = nativeLinkSource.Select(link =>
            {
                var start = NativeCharacterOffset(link.StartByte); var end = NativeCharacterOffset(checked(link.StartByte + link.LengthByte));
                return new EditorRichTextLink(start, Math.Max(0, end - start), link.Address);
            }).ToArray();
        }
        if (replaced || !SameNativeRanges(nativeHighlightSource, highlights))
        {
            nativeHighlightSource = highlights?.ToArray() ?? [];
            nativeHighlights = nativeHighlightSource.Select(highlight =>
            {
                var start = NativeCharacterOffset(highlight.StartByte); var end = NativeCharacterOffset(checked(highlight.StartByte + highlight.LengthByte));
                return new EditorRichTextHighlight(start, Math.Max(0, end - start));
            }).ToArray();
        }
        var width = Math.Max(1, requestedSize?.X ?? Gui.GetContentRegionAvail().X);
        var size = requestedSize ?? new Vector2(width, Gui.GetTextLineHeight() * 8 + Gui.GetStyle().FramePadding.Y * 2);
        nativeTextView.Draw(id, text, [], nativeLinks, size, default, openLink ?? (static _ => { }), expandHeightToContent, wrap, followTail, nativeHighlights, autoHeight: requestedSize == null);
        Active = nativeTextView.Active;
        selectionStart = nativeUtf8Offsets[Math.Clamp(nativeTextView.SelectionStart, 0, text.Length)];
        selectionEnd = nativeUtf8Offsets[Math.Clamp(nativeTextView.SelectionEnd, 0, text.Length)];
        caret = nativeUtf8Offsets[Math.Clamp(nativeTextView.Caret, 0, text.Length)];
    }
    private static bool SameNativeRanges<T>(T[] previous, IReadOnlyList<T>? current)
    {
        if (previous.Length != (current?.Count ?? 0)) return false;
        for (var index = 0; index < previous.Length; index++)
            if (!EqualityComparer<T>.Default.Equals(previous[index], current![index])) return false;
        return true;
    }
    private int NativeCharacterOffset(int sourceByte)
    {
        sourceByte = Math.Clamp(sourceByte, 0, original.Length); var low = 0; var high = nativeUtf8Offsets.Length;
        while (low < high) { var middle = low + (high - low) / 2; if (nativeUtf8Offsets[middle] < sourceByte) low = middle + 1; else high = middle; }
        return Math.Min(low, source.Length);
    }
    private int NativeCaretLine()
    {
        var end = Math.Clamp(nativeTextView!.Caret, 0, source.Length); var line = 0;
        for (var index = 0; index < end; index++)
            if (source[index] == '\n' || source[index] == '\r' && (index + 1 >= source.Length || source[index + 1] != '\n')) line++;
        return line;
    }
    internal void SuspendNativePresentation()
    {
        nativeTextView?.Suspend();
        if (nativeTextView != null) { nativeTextView.GlyphImages = null; nativeTextView.GlyphCommands = null; }
        NativeGlyphImages = null; NativeGlyphCommands = null; Active = false;
    }
    public void Dispose()
    {
        nativeTextView?.Dispose(); nativeTextView = null; NativeGlyphImages = null; NativeGlyphCommands = null; Active = false;
    }
}
