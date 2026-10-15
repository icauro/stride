using System.Text;
using System.Text.RegularExpressions;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed record EditorSelectableTextLink(int StartByte, int LengthByte, string Address);

/// <summary>Native read-only selection with source-preserving display wrapping.</summary>
internal sealed unsafe partial class EditorSelectableText : IDisposable
{
    private string source = "";
    private byte[] original = [], buffer = [0];
    private int[] sourceOffsets = [0];
    private int[] sourceLineStarts = [0];
    private float wrapWidth = -1, fontSize;
    private int selectionStart, selectionEnd, caret;
    private int displayedLines = 1;
    private ImGuiWindowPtr textWindow;
    private uint widgetId;
    private readonly ImGuiInputTextCallback callback;
    private string? pressedLink;
    private Vector2 pressedLinkPosition;
    internal bool Active { get; private set; }
    internal float? FollowTailToleranceDips { get; set; }
    internal bool FollowTailWithSelection { get; set; }
    internal int CaretLine
    {
        get
        {
            if (nativeLastDraw && nativeTextView != null) return NativeCaretLine();
            var end = sourceOffsets[Math.Clamp(caret, 0, sourceOffsets.Length - 1)];
            return FindLine(sourceLineStarts, end);
        }
    }
    internal EditorSelectableText() => callback = ReadSelection;

    private int ReadSelection(ImGuiInputTextCallbackData* data)
    {
        if (requestedSelection is { } requested)
        {
            data->SelectionStart = FindDisplayOffset(requested.Start);
            data->SelectionEnd = FindDisplayOffset(requested.End);
            data->CursorPos = data->SelectionEnd;
            requestedSelection = null; revealSelection = true;
        }
        selectionStart = data->SelectionStart; selectionEnd = data->SelectionEnd; caret = data->CursorPos;
        return 0;
    }

    internal void Draw(string id, string text, bool wrap, Vector2? requestedSize = null, bool followTail = false,
        IReadOnlyList<EditorSelectableTextLink>? links = null, Action<string>? openLink = null, bool expandHeightToContent = false,
        IReadOnlyList<EditorSelectableTextHighlight>? highlights = null)
    {
        nativeLastDraw = UseNativeShaping;
        if (UseNativeShaping)
        {
            DrawNativeReadonly(id, text, wrap, requestedSize, followTail, links, openLink, expandHeightToContent, highlights);
            return;
        }
        var padding = Gui.GetStyle().FramePadding;
        var currentId = Gui.GetID(id);
        var idChanged = widgetId != currentId;
        if (idChanged) { widgetId = currentId; textWindow = default; selectionStart = selectionEnd = caret = 0; }
        var width = Math.Max(1, requestedSize?.X ?? Gui.GetContentRegionAvail().X);
        var available = wrap ? Math.Max(1, width - padding.X * 2 - Gui.GetStyle().ScrollbarSize) : 0;
        var changed = idChanged || source != text || wrapWidth != available || fontSize != Gui.GetFontSize();
        var tolerance = FollowTailToleranceDips is { } dips ? dips * Gui.GetStyle().FontScaleDpi : Gui.GetTextLineHeight();
        var tail = textWindow.IsNull || textWindow.ScrollMax.Y - textWindow.Scroll.Y < tolerance;
        var selected = selectionStart != selectionEnd;
        if (changed)
        {
            source = text; wrapWidth = available; fontSize = Gui.GetFontSize();
            Rebuild(available);
            selectionStart = selectionEnd = caret = 0;
            var input = ImGuiP.GetInputTextState(Gui.GetID(id));
            if (input.Handle != null) ImGuiP.ReloadUserBufAndKeepSelection(input);
        }
        var height = Math.Min(8, displayedLines) * Gui.GetTextLineHeight() + padding.Y * 2 + 4 * Gui.GetStyle().FontScaleDpi;
        var size = requestedSize ?? new Vector2(width, height);
        if (expandHeightToContent) size.Y = (displayedLines + 1) * Gui.GetTextLineHeight() + padding.Y * 2 + 4 * Gui.GetStyle().FontScaleDpi;
        var flags = ImGuiInputTextFlags.ReadOnly | ImGuiInputTextFlags.CallbackAlways | ImGuiInputTextFlags.NoUndoRedo;
        using var frameColor = ImGuiScopes.Color(ImGuiCol.FrameBg, System.Numerics.Vector4.Zero);
        using var frameBorder = ImGuiScopes.Style(ImGuiStyleVar.FrameBorderSize, 0);
        var richLinks = links is { Count: > 0 } && openLink != null;
        var coloredText = !richLinks && NativeTextColors.Count > 0;
        if (richLinks || coloredText) Gui.PushStyleColor(ImGuiCol.Text, System.Numerics.Vector4.Zero);
        try
        {
            if (focusSelection) { Gui.SetKeyboardFocusHere(); focusSelection = false; }
            fixed (byte* bytes = buffer) Gui.InputTextMultiline(id, bytes, (nuint)buffer.Length, size, flags, callback);
        }
        finally { if (richLinks || coloredText) Gui.PopStyleColor(); }
        Active = Gui.IsItemActive();
        if (!Active && requestedSelection is { } pending)
        {
            selectionStart = FindDisplayOffset(pending.Start);
            selectionEnd = caret = FindDisplayOffset(pending.End);
            requestedSelection = null; revealSelection = true;
        }
        if (textWindow.IsNull)
        {
            var windows = Gui.GetCurrentContext().Windows;
            var childId = Gui.GetID(id);
            for (var index = 0; index < windows.Size; index++)
                if (!windows[index].IsNull && windows[index].ChildId == childId) { textWindow = windows[index]; break; }
        }
        if (followTail && changed && tail && (!selected || FollowTailWithSelection) && !textWindow.IsNull)
            ImGuiP.SetScrollY(textWindow, displayedLines * Gui.GetTextLineHeight() + padding.Y * 2);
        if (revealSelection && !textWindow.IsNull)
        {
            var selectedLine = FindLine(displayLineStarts, Math.Clamp(selectionStart, 0, buffer.Length - 1));
            ImGuiP.SetScrollY(textWindow, Math.Max(0, selectedLine * Gui.GetTextLineHeight() - (textWindow.InnerRect.Max.Y - textWindow.InnerRect.Min.Y) / 2));
            revealSelection = false;
        }
        if (highlights is { Count: > 0 } && !textWindow.IsNull) DrawHighlights(highlights, paintText: !coloredText && !richLinks);
        if (coloredText && !textWindow.IsNull) DrawColoredText();
        if (richLinks && !textWindow.IsNull) DrawLinks(links!, openLink!);
        if (Active && Gui.GetIO().KeyCtrl && Gui.IsKeyPressed(ImGuiKey.C)) CopySelection();
        if (Gui.BeginPopupContextItem(id + "/copy"))
        {
            try { if (Gui.MenuItem(EditorLocalization.Widget("Copy", "Menu"), "Ctrl+C", false, source.Length > 0)) CopySelection(); }
            finally { Gui.EndPopup(); }
        }
    }

    private void DrawLinks(IReadOnlyList<EditorSelectableTextLink> links, Action<string> openLink)
    {
        var draw = textWindow.DrawList;
        var clip = textWindow.InnerClipRect;
        var origin = textWindow.Pos + textWindow.WindowPadding - textWindow.Scroll;
        var height = Gui.GetTextLineHeight(); var mouse = Gui.GetIO().MousePos;
        string? hoveredAddress = null;
        draw.PushClipRect(clip.Min, clip.Max, true);
        try
        {
            var firstLine = Math.Clamp((int)Math.Floor((clip.Min.Y - origin.Y) / height), 0, displayLineStarts.Length - 1);
            var lastLine = Math.Clamp((int)Math.Ceiling((clip.Max.Y - origin.Y) / height), firstLine, displayLineStarts.Length - 1);
            for (var line = firstLine; line <= lastLine; line++)
            {
                var start = displayLineStarts[line];
                var end = line + 1 < displayLineStarts.Length ? displayLineStarts[line + 1] - 1 : buffer.Length - 1;
                if (origin.Y + (line + 1) * height >= clip.Min.Y && origin.Y + line * height <= clip.Max.Y)
                {
                    var drawn = start;
                    void PlainText(int until)
                    {
                        if (until <= drawn) return;
                        var prefix = Encoding.UTF8.GetString(buffer.AsSpan(start, drawn - start));
                        var label = Encoding.UTF8.GetString(buffer.AsSpan(drawn, until - drawn));
                        draw.AddText(origin + new Vector2(Gui.CalcTextSize(prefix).X, line * height), Gui.GetColorU32(ImGuiCol.Text), label);
                        drawn = until;
                    }
                    foreach (var link in links)
                    {
                        if (link.StartByte >= sourceOffsets[end] || link.StartByte + link.LengthByte <= sourceOffsets[start]) continue;
                        var first = Math.Clamp(FindDisplayOffset(link.StartByte), start, end);
                        var last = Math.Clamp(FindDisplayOffset(link.StartByte + link.LengthByte), first, end);
                        if (last == first || sourceOffsets[first] >= link.StartByte + link.LengthByte) continue;
                        PlainText(first);
                        var before = Encoding.UTF8.GetString(buffer.AsSpan(start, first - start));
                        var label = Encoding.UTF8.GetString(buffer.AsSpan(first, last - first));
                        var position = origin + new Vector2(Gui.CalcTextSize(before).X, line * height);
                        var size = Gui.CalcTextSize(label);
                        var selectionLow = Math.Min(selectionStart, selectionEnd); var selectionHigh = Math.Max(selectionStart, selectionEnd);
                        var color = Gui.GetColorU32(selectionLow < last && selectionHigh > first ? ImGuiCol.Text : ImGuiCol.TextLink);
                        draw.AddText(position, color, label);
                        draw.AddLine(position + new Vector2(0, height - 1), position + new Vector2(size.X, height - 1), color);
                        drawn = last;
                        if (Gui.IsItemHovered() && mouse.X >= Math.Max(position.X, clip.Min.X) && mouse.X <= Math.Min(position.X + size.X, clip.Max.X)
                            && mouse.Y >= Math.Max(position.Y, clip.Min.Y) && mouse.Y <= Math.Min(position.Y + height, clip.Max.Y)) hoveredAddress = link.Address;
                    }
                    PlainText(end);
                }
            }
        }
        finally { draw.PopClipRect(); }
        if (hoveredAddress != null) Gui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (Gui.IsMouseClicked(ImGuiMouseButton.Left)) { pressedLink = hoveredAddress; pressedLinkPosition = mouse; }
        if (Gui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            var activate = pressedLink != null && pressedLink == hoveredAddress && Vector2.DistanceSquared(pressedLinkPosition, mouse) < 16
                && selectionStart == selectionEnd;
            var address = pressedLink; pressedLink = null;
            if (activate) openLink(address!);
        }
    }

    private void CopySelection()
    {
        var start = sourceOffsets[Math.Clamp(Math.Min(selectionStart, selectionEnd), 0, sourceOffsets.Length - 1)];
        var end = sourceOffsets[Math.Clamp(Math.Max(selectionStart, selectionEnd), 0, sourceOffsets.Length - 1)];
        var text = start == end ? source : Encoding.UTF8.GetString(original.AsSpan(start, end - start));
        WindowsScriptClipboard.TryWrite((nint)Gui.GetWindowViewport().PlatformHandleRaw, text, false);
    }

    private void Rebuild(float width)
    {
        original = Encoding.UTF8.GetBytes(source);
        var sourceLines = new List<int> { 0 };
        for (var index = 0; index < original.Length; index++)
            if (original[index] == (byte)'\n' || original[index] == (byte)'\r'
                && (index + 1 >= original.Length || original[index + 1] != (byte)'\n')) sourceLines.Add(index + 1);
        sourceLineStarts = sourceLines.ToArray();
        var display = new List<byte>(original.Length + 1);
        var offsets = new List<int>(original.Length + 1) { 0 };
        void Publish()
        {
            displayedLines = display.Count(value => value == (byte)'\n') + 1;
            displayLineStarts = new[] { 0 }.Concat(display.Select((value, index) => (value, index))
                .Where(item => item.value == (byte)'\n').Select(item => item.index + 1)).ToArray();
            display.Add(0);
            buffer = GC.AllocateUninitializedArray<byte>(display.Count, pinned: true);
            display.CopyTo(buffer); sourceOffsets = offsets.ToArray();
        }
        if (width == 0)
        {
            for (var index = 0; index < original.Length; index++)
            {
                var value = original[index];
                if (value == 0)
                {
                    // A native C-string surface must not truncate copied log/message source.
                    display.AddRange(Encoding.UTF8.GetBytes("[NUL]"));
                    for (var glyph = 0; glyph < 4; glyph++) offsets.Add(index);
                    offsets.Add(index + 1);
                    continue;
                }
                if (value == (byte)'\r')
                {
                    value = (byte)'\n';
                    if (index + 1 < original.Length && original[index + 1] == (byte)'\n') index++;
                }
                display.Add(value); offsets.Add(index + 1);
            }
            Publish(); return;
        }
        var bytePosition = 0;
        var line = "";
        void Break()
        {
            display.Add((byte)'\n'); offsets.Add(bytePosition); line = "";
        }
        void Append(string value)
        {
            foreach (var rune in value.EnumerateRunes())
            {
                var glyph = rune.Value == 0 ? "[NUL]" : rune.ToString();
                if (width > 0 && line.Length > 0 && Gui.CalcTextSize(line + glyph).X > width) Break();
                if (rune.Value == 0)
                {
                    display.AddRange(Encoding.UTF8.GetBytes(glyph));
                    for (var index = 0; index < glyph.Length - 1; index++) offsets.Add(bytePosition);
                    offsets.Add(++bytePosition);
                }
                else foreach (var valueByte in Encoding.UTF8.GetBytes(glyph)) { display.Add(valueByte); offsets.Add(++bytePosition); }
                if (width > 0) line += glyph;
            }
        }
        foreach (Match token in Regex.Matches(source, @"\r\n|\r|\n|[^\S\r\n]+|[^\s]+"))
        {
            var word = token.Value;
            if (word is "\r\n" or "\r" or "\n")
            {
                bytePosition += Encoding.UTF8.GetByteCount(word);
                display.Add((byte)'\n'); offsets.Add(bytePosition); line = "";
            }
            else
            {
                if (width > 0 && line.Length > 0 && !char.IsWhiteSpace(word[0]) && Gui.CalcTextSize(line + word).X > width) Break();
                Append(word);
            }
        }
        Publish();
    }
}
