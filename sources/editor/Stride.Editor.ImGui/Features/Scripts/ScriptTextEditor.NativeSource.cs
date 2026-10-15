using System.Numerics;
using System.Text;
using Hexa.NET.ImGui;
using Microsoft.CodeAnalysis.Text;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed unsafe partial class ScriptTextEditor
{
    private bool sourceFocused, sourceDragging, sourceReveal;
    private uint sourceSurfaceId;
    private int sourceAnchor;
    private bool sourceDragWords, sourceDragLines;
    private int sourceWordAnchorStart, sourceWordAnchorEnd;
    private float desiredSourceX = -1;
    private int sourcePageLines = 1;
    private long sourceCaretChanged;
    private bool sourceHadFocus;
    private bool DrawNativeSource(ScriptSourceDocument document)
    {
        EnsureSourceGeometry();
        var height = nativeSourceHeight; var size = Gui.GetContentRegionAvail();
        var bodyHeight = Math.Max(height, size.Y - Gui.GetStyle().FramePadding.Y * 2);
        var virtualWidth = rectangle is { } bounds ? Math.Max(bounds.AnchorColumn, bounds.EndColumn) * MeasureGlyph(" ") : 0;
        Gui.SetNextWindowContentSize(new(Math.Max(size.X, Math.Max(measuredSourceWidth, virtualWidth) + Gui.GetStyle().FramePadding.X * 2),
            lines.Length * height + Math.Max(0, bodyHeight - height)));
        Gui.PushStyleVar(ImGuiStyleVar.WindowPadding, Gui.GetStyle().FramePadding);
        var visible = Gui.BeginChild(label, size, ImGuiChildFlags.FrameStyle, ImGuiWindowFlags.HorizontalScrollbar);
        Gui.PopStyleVar();
        if (!visible) { InputActive = false; sourceHadFocus = false; sourceDragging = false; possibleSourceDrag = false; if (sourceDropBinding != null) sourceDropBinding.Transfer = null; ReleaseNativeSourceFocus(); ImGuiScopes.EndChild(); return false; }
        var child = ImGuiP.GetCurrentWindow(); var clip = child.InnerClipRect;
        if (nativeDropCursor != null)
        {
            if (nativeDropScroll.Y != 0) ImGuiP.SetScrollY(child, Math.Max(0, child.Scroll.Y + nativeDropScroll.Y));
            if (nativeDropScroll.X != 0) ImGuiP.SetScrollX(child, Math.Max(0, child.Scroll.X + nativeDropScroll.X));
        }
        var origin = child.Pos + Gui.GetStyle().FramePadding - child.Scroll;
        Gui.SetCursorScreenPos(clip.Min);
        Gui.InvisibleButton("##SourceSurface", Vector2.Max(new(1, 1), clip.Max - clip.Min), ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        sourceSurfaceId = Gui.GetItemID();
        var hovered = Gui.IsItemHovered(); var active = ImGuiP.GetActiveID();
        var pointerFocusRequest = hovered && (Gui.IsMouseClicked(ImGuiMouseButton.Left) || Gui.IsMouseClicked(ImGuiMouseButton.Right));
        var presentationOwnsFocus = CanEditPresentation?.Invoke() == true;
        // A visible source in another document group must not reassert its old
        // caret focus while a different canonical document owns commands.
        // A real pointer click may initiate the native group focus transition.
        if (!presentationOwnsFocus && !pointerFocusRequest)
        {
            sourceFocused = false;
            ReleaseNativeSourceFocus();
        }
        if (!WindowsSourceTextDragDrop.IsDragging && (hovered || sourceDragging)) Gui.SetMouseCursor(ImGuiMouseCursor.TextInput);
        if ((presentationOwnsFocus || pointerFocusRequest) && (Gui.IsItemFocused() || focusSource && presentationOwnsFocus))
        {
            var requestedFocus = focusSource && presentationOwnsFocus; sourceFocused = true;
            if (requestedFocus) focusSource = false;
            if (requestedFocus) ImGuiP.FocusWindow(child);
            ImGuiP.SetFocusID(sourceSurfaceId, child);
        }
        if (active != 0 && active != sourceSurfaceId && !Gui.IsItemActive()) sourceFocused = false;
        // Docked sibling groups can share a native root. Only this actual source
        // child (or its genuine children) constitutes source keyboard focus.
        var nativeFocus = Gui.GetCurrentContext().NavWindow;
        var nativeSourceFocused = false;
        for (var depth = 0; !nativeFocus.IsNull && depth < 128; depth++, nativeFocus = nativeFocus.ParentWindow)
            if (nativeFocus.ID == child.ID) { nativeSourceFocused = true; break; }
        if (!nativeSourceFocused) sourceFocused = false;
        // Native InputText releases its active ID on an outside click. This source
        // body can run before a later sibling tab, so release BEFORE reasserting
        // keyboard focus; otherwise the held click blocks that tab in this frame.
        if (sourceFocused && Gui.GetIO().MouseClicked[0] && !hovered && !sourceDragging
            && !WindowsSourceTextDragDrop.IsDragging
            && (ImGuiP.GetActiveID() == 0 || ImGuiP.GetActiveID() == sourceSurfaceId))
        {
            sourceFocused = false;
            ReleaseNativeSourceFocus();
        }
        if (pointerFocusRequest)
        {
            sourceFocused = true; ImGuiP.SetFocusID(sourceSurfaceId, child); ImGuiP.SetActiveID(sourceSurfaceId, child);
            var point = Gui.GetMousePos() - origin; var row = Math.Clamp((int)MathF.Floor(point.Y / height), 0, lines.Length - 1);
            var position = lineStarts[row] + SourceHit(row, point.X);
            if (Gui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                pendingSelection = null; pendingCursor = null; selectedSearch = -1; desiredSourceX = -1;
                sourceDragWords = false; sourceDragLines = false;
                if (!Gui.GetIO().KeyShift && !Gui.GetIO().KeyAlt && !Gui.GetIO().KeyCtrl && Gui.GetMouseClickedCount(ImGuiMouseButton.Left) == 1
                    && IsSourceSelectionHit(row, position, point.X))
                {
                    possibleSourceDrag = true; possibleSourceDragPoint = Gui.GetMousePos(); possibleSourceDragPosition = position;
                }
                else if (Gui.GetIO().KeyAlt)
                {
                    var column = Math.Max(0, point.X) / Math.Max(1, MeasureGlyph(" "));
                    rectangle = Gui.GetIO().KeyShift && rectangle is { } previousBox
                        ? previousBox with { EndLine = row, EndColumn = column } : new(row, column, row, column);
                    SyncRectangleCaret(document);
                }
                else
                {
                    var oldSelection = selection;
                    rectangle = null;
                    var clicks = Gui.GetMouseClickedCount(ImGuiMouseButton.Left);
                    if (clicks >= 2 || Gui.GetIO().KeyCtrl)
                    {
                        sourceDragLines = clicks == 3;
                        var range = sourceDragLines ? SourceLineRange(row) : SourceWordRange(document.EditorText, lineStarts[row] + SourceElementHit(row, point.X));
                        if (Gui.GetIO().KeyShift && oldSelection.Length > 0)
                        {
                            if (range.Start < oldSelection.Start) SetSourceCaret(document, range.Start, sourceAnchor);
                            else if (range.End > oldSelection.End) SetSourceCaret(document, range.End, sourceAnchor);
                        }
                        else SetSourceCaret(document, range.End, range.Start);
                        sourceWordAnchorStart = selection.Start; sourceWordAnchorEnd = selection.End; sourceDragWords = true;
                    }
                    else SetSourcePointCaret(document, row, point.X, Gui.GetIO().KeyShift ? sourceAnchor : -1);
                }
                sourceDragging = !possibleSourceDrag;
                ImGuiP.SetKeyOwner(ImGuiKey.MouseLeft, sourceSurfaceId, (ImGuiInputFlags)ImGuiInputFlagsPrivate.LockUntilRelease);
            }
            else if (selection.Length == 0 && rectangle == null) { sourceAnchor = position; SetSourceCaret(document, position, position); }
        }
        if (sourceDragging)
        {
            var held = ImGuiP.IsMouseDown(ImGuiMouseButton.Left, sourceSurfaceId);
            if (!held && !Gui.IsMouseReleased(ImGuiMouseButton.Left)) sourceDragging = false;
            else
            {
                var point = Gui.GetMousePos() - origin; var row = Math.Clamp((int)MathF.Floor(point.Y / height), 0, lines.Length - 1);
                if (rectangle is { } box) { rectangle = box with { EndLine = row, EndColumn = Math.Max(0, point.X) / Math.Max(1, MeasureGlyph(" ")) }; SyncRectangleCaret(document); }
                else
                {
                    var position = lineStarts[row] + SourceHit(row, point.X);
                    if (sourceDragWords)
                    {
                        var word = sourceDragLines ? SourceLineRange(row) : SourceWordRange(document.EditorText, lineStarts[row] + SourceElementHit(row, point.X));
                        SetSourceCaret(document, word.Start < sourceWordAnchorStart ? word.Start : Math.Max(word.End, sourceWordAnchorEnd),
                            word.Start < sourceWordAnchorStart ? sourceWordAnchorEnd : sourceWordAnchorStart);
                    }
                    else SetSourcePointCaret(document, row, point.X, sourceAnchor);
                }
                if (Gui.GetMousePos().Y < clip.Min.Y) ImGuiP.SetScrollY(child, Math.Max(0, child.Scroll.Y - height));
                else if (Gui.GetMousePos().Y > clip.Max.Y) ImGuiP.SetScrollY(child, child.Scroll.Y + height);
                if (Gui.GetMousePos().X < clip.Min.X) ImGuiP.SetScrollX(child, Math.Max(0, child.Scroll.X - height));
                else if (Gui.GetMousePos().X > clip.Max.X) ImGuiP.SetScrollX(child, child.Scroll.X + height);
                // Drag scrolling owns the viewport; caret reveal must not counteract it.
                sourceReveal = false;
                if (!held) sourceDragging = false;
            }
        }
        ContinueSourceDrag(document);
        if (sourceFocused && (ImGuiP.GetActiveID() == 0 || ImGuiP.GetActiveID() == sourceSurfaceId))
        {
            ImGuiP.SetActiveID(sourceSurfaceId, child);
            ImGuiP.SetFocusID(sourceSurfaceId, child);
            // Match native InputText: keyboard focus may persist while other controls
            // receive hover/clicks. Retain exclusive capture during source selection.
            Gui.GetCurrentContext().ActiveIdAllowOverlap = !Gui.GetIO().MouseDown[0] && !sourceDragging;
        }
        InputActive = sourceFocused && ImGuiP.GetActiveID() == sourceSurfaceId && !WindowsSourceTextDragDrop.IsDragging;
        if (InputActive && !sourceHadFocus) sourceCaretChanged = Environment.TickCount64;
        sourceHadFocus = InputActive;
        sourcePageLines = Math.Max(1, (int)((clip.Max.Y - clip.Min.Y) / height));
        ConsumeSourceNavigation(document);
        var previousVersion = document.Version;
        if (InputActive) EditSourceKeyboard(document);
        if (version != document.Version) { Load(document.EditorText); version = document.Version; }
        var draw = child.DrawList; draw.PushClipRect(clip.Min, clip.Max, true);
        DrawNativeSourceSelection(document, draw, origin, clip.Min, clip.Max, height);
        DrawSourceGlyphs(draw, origin, clip.Min, clip.Max, height);
        ObserveSourceDropSurface(document, origin, clip.Min, clip.Max, height);
        var caretLine = SourceRow(caretPosition); var caretX = SourceX(caretLine, caretPosition - lineStarts[caretLine]);
        if (rectangle is { } virtualCaret) caretX = virtualCaret.EndColumn * MeasureGlyph(" ");
        var caret = origin + new Vector2(caretX, caretLine * height);
        if (nativeDropCursor is { } dropCursor)
        {
            var dropPoint = origin + new Vector2(dropCursor.X, SourceRow(dropCursor.Position) * height);
            draw.AddLine(dropPoint, dropPoint + new Vector2(0, height), Gui.GetColorU32(ImGuiCol.Text), 1);
        }
        if (InputActive)
        {
            if (((Environment.TickCount64 - sourceCaretChanged) / 500 & 1) == 0)
            {
                if (overwriteSource && rectangle == null && caretPosition - lineStarts[caretLine] < lines[caretLine].Length)
                {
                    var end = Math.Min(lines[caretLine].Length, NextSourcePosition(caretPosition, document.EditorText.Length) - lineStarts[caretLine]);
                    foreach (var range in SourceRangeRectangles(caretLine, caretPosition - lineStarts[caretLine], end))
                        draw.AddRectFilled(origin + new Vector2(range.Minimum.X, caretLine * height), origin + new Vector2(range.Maximum.X, (caretLine + 1) * height), Gui.GetColorU32(ImGuiCol.Text));
                }
                else draw.AddLine(caret, caret + new Vector2(0, height), Gui.GetColorU32(ImGuiCol.Text), 1);
            }
            var context = Gui.GetCurrentContext();
            context.Handle->ActiveIdUsingNavDirMask = 15;
            context.Handle->PlatformImeData.WantVisible = MayEditSource(document) ? (byte)1 : (byte)0;
            context.Handle->PlatformImeData.WantTextInput = MayEditSource(document) ? (byte)1 : (byte)0;
            context.Handle->PlatformImeData.InputPos = caret; context.Handle->PlatformImeData.InputLineHeight = height;
            context.Handle->PlatformImeData.ViewportId = Gui.GetWindowViewport().ID;
            Gui.GetIO().WantTextInput = MayEditSource(document);
        }
        draw.PopClipRect();
        if (sourceReveal)
        {
            if (caret.Y < clip.Min.Y || caret.Y + height > clip.Max.Y) ImGuiP.SetScrollY(child, Math.Max(0, caretLine * height - (clip.Max.Y - clip.Min.Y) / 2));
            if (caret.X < clip.Min.X || caret.X > clip.Max.X) ImGuiP.SetScrollX(child, Math.Max(0, caretX - (clip.Max.X - clip.Min.X) / 2));
            sourceReveal = false;
        }
        DrawSourceContextMenu(document);
        ImGuiScopes.EndChild();
        return document.Version != previousVersion;
    }
    private int SourceRow(int position)
    { var row = Array.BinarySearch(lineStarts, position); return Math.Clamp(row < 0 ? ~row - 1 : row, 0, lines.Length - 1); }
    private void SetSourceCaret(ScriptSourceDocument document, int position, int anchor)
    {
        sourceVisualCaret = null;
        position = SnapSourcePosition(Math.Clamp(position, 0, document.EditorText.Length));
        anchor = SnapSourcePosition(Math.Clamp(anchor, 0, document.EditorText.Length));
        if (caretPosition != position || sourceAnchor != anchor) sourceCaretChanged = Environment.TickCount64;
        caretPosition = position; sourceAnchor = anchor; selection = TextSpan.FromBounds(Math.Min(position, anchor), Math.Max(position, anchor));
        caretByte = selectionEndByte = Encoding.UTF8.GetByteCount(document.EditorText.AsSpan(0, position));
        selectionStartByte = Encoding.UTF8.GetByteCount(document.EditorText.AsSpan(0, anchor)); sourceReveal = true;
    }
    private void ConsumeSourceNavigation(ScriptSourceDocument document)
    {
        if (pendingSelection is { } found)
        {
            var start = Encoding.UTF8.GetCharCount(new ReadOnlySpan<byte>(buffer, Math.Clamp(found.Start, 0, bufferLength)));
            var end = Encoding.UTF8.GetCharCount(new ReadOnlySpan<byte>(buffer, Math.Clamp(found.End, 0, bufferLength)));
            rectangle = null; SetSourceCaret(document, pendingSelectionCaretAtEnd ? end : start, pendingSelectionCaretAtEnd ? start : end);
            pendingSelection = null; pendingSelectionCaretAtEnd = false;
        }
        if (pendingCursor is { } cursor)
        {
            var position = Encoding.UTF8.GetCharCount(new ReadOnlySpan<byte>(buffer, Math.Clamp(cursor, 0, bufferLength)));
            rectangle = null; SetSourceCaret(document, position, position); pendingCursor = null;
        }
        if (queuedDropRectangle is { } dropped)
        { rectangle = dropped; queuedDropRectangle = null; SyncRectangleCaret(document); }
    }
    private bool SourcePressed(ImGuiKey key) => ImGuiP.IsKeyPressed(key, ImGuiInputFlags.Repeat, sourceSurfaceId)
        || key == ImGuiKey.Enter && ImGuiP.IsKeyPressed(ImGuiKey.KeypadEnter, ImGuiInputFlags.Repeat, sourceSurfaceId);
    private static bool SourceEnterPressed(bool repeat) => Gui.IsKeyPressed(ImGuiKey.Enter, repeat) || Gui.IsKeyPressed(ImGuiKey.KeypadEnter, repeat);
    private void EditSourceKeyboard(ScriptSourceDocument document)
    {
        var io = Gui.GetIO();
        if (io.KeyCtrl && SourcePressed(ImGuiKey.A)) { rectangle = null; SetSourceCaret(document, document.EditorText.Length, 0); }
        if (MayEditSource(document) && completion != null && !io.KeyCtrl && !io.KeyAlt && SourcePressed(ImGuiKey.Tab)) assistRequest = new(ScriptAssistKind.CommitCompletion, caretPosition, document.Version, completion, selectedCompletion);
        else if (io.KeyAlt && io.KeyShift && ((!io.KeyCtrl && (SourcePressed(ImGuiKey.UpArrow) || SourcePressed(ImGuiKey.DownArrow) || SourcePressed(ImGuiKey.Home) || SourcePressed(ImGuiKey.End))) || SourcePressed(ImGuiKey.LeftArrow) || SourcePressed(ImGuiKey.RightArrow))) MoveSourceRectangle(document);
        else if (!preserveCompletionCursor && !io.KeyAlt)
        {
            var next = caretPosition; var row = SourceRow(next); var column = next - lineStarts[row];
            (int Position, float X)? visualTarget = null;
            var navigation = false;
            if (SourcePressed(ImGuiKey.LeftArrow)) { navigation = true; if (io.KeyCtrl) next = SourceWord(document.EditorText, next, false); else { visualTarget = VisualSourceMove(next, false); next = visualTarget.Value.Position; } desiredSourceX = -1; }
            else if (SourcePressed(ImGuiKey.RightArrow)) { navigation = true; if (io.KeyCtrl) next = SourceWord(document.EditorText, next, true); else { visualTarget = VisualSourceMove(next, true); next = visualTarget.Value.Position; } desiredSourceX = -1; }
            else if (SourcePressed(ImGuiKey.Home)) { navigation = true; next = io.KeyCtrl ? 0 : SourceLineHome(row, next); desiredSourceX = -1; }
            else if (SourcePressed(ImGuiKey.End)) { navigation = true; next = io.KeyCtrl ? document.EditorText.Length : lineStarts[row] + lines[row].Length; desiredSourceX = -1; }
            else if (!io.KeyCtrl && (SourcePressed(ImGuiKey.UpArrow) || SourcePressed(ImGuiKey.DownArrow) || SourcePressed(ImGuiKey.PageUp) || SourcePressed(ImGuiKey.PageDown)))
            {
                navigation = true;
                if (desiredSourceX < 0) desiredSourceX = SourceX(row, column);
                var page = sourcePageLines;
                var delta = SourcePressed(ImGuiKey.UpArrow) ? -1 : SourcePressed(ImGuiKey.DownArrow) ? 1 : SourcePressed(ImGuiKey.PageUp) ? -page : page;
                row = Math.Clamp(row + delta, 0, lines.Length - 1);
                var hit = SourceGeometry(row).Native.HitCaret(new(desiredSourceX, nativeSourceHeight / 2));
                next = lineStarts[row] + hit.Position; visualTarget = (next, hit.X);
            }
            if (navigation) { rectangle = null; SetSourceCaret(document, next, io.KeyShift ? sourceAnchor : next); sourceVisualCaret = visualTarget; }
        }
        if (!MayEditSource(document)) return;
        if (!io.KeyCtrl && !io.KeyAlt && SourcePressed(ImGuiKey.Enter) && handledSourceCharacter != '\n' && !commitOnEnter && !codeActionShortcut) EnterSourceLine(document);
        if (!io.KeyCtrl && !io.KeyAlt && SourcePressed(ImGuiKey.Tab) && handledSourceCharacter != '\t' && completion == null) IndentSource(document, io.KeyShift);
        if (SourcePressed(ImGuiKey.Backspace) || SourcePressed(ImGuiKey.Delete))
        {
            if (rectangle != null) ReplaceRectangle(document, "", SourcePressed(ImGuiKey.Backspace) ? -1 : 1);
            else
            {
                var start = selection.Start; var end = selection.End;
                if (selection.Length == 0)
                {
                    if (SourcePressed(ImGuiKey.Backspace)) start = io.KeyCtrl ? SourceWord(document.EditorText, start, false) : PreviousSourceCodepoint(document.EditorText, start);
                    else end = io.KeyCtrl ? SourceWord(document.EditorText, end, true) : NextSourcePosition(end, document.EditorText.Length);
                }
                ReplaceNativeSource(document, "", TextSpan.FromBounds(start, end));
            }
        }
        var input = new StringBuilder();
        for (var index = 0; index < io.InputQueueCharacters.Size; index++)
        {
            var scalar = (int)io.InputQueueCharacters[index];
            if (scalar is '\n' or '\r' or '\t' or '\b' or 27 || io.KeyCtrl && !io.KeyAlt || !Rune.TryCreate(scalar, out var rune)) continue;
            input.Append(rune.ToString());
        }
        if (input.Length > 0)
        {
            var value = input.ToString();
            if (value.Length == 1 && completion != null && CompletionCommitsCharacter(value[0]))
                typedCompletionCommit = new(ScriptAssistKind.CommitCompletion, caretPosition, document.Version, completion, selectedCompletion, CommitCharacter: value[0]);
            if (rectangle != null) ReplaceRectangle(document, value);
            else
            {
                var span = selection;
                if (span.Length == 0 && overwriteSource && span.Start < document.EditorText.Length && document.EditorText[span.Start] != '\n')
                    span = TextSpan.FromBounds(span.Start, NextSourcePosition(span.Start, document.EditorText.Length));
                ReplaceNativeSource(document, value, span);
            }
        }
    }
    private void ReplaceNativeSource(ScriptSourceDocument document, string value, TextSpan span)
    {
        if (!MayEditSource(document)) return;
        document.UpdateEditorChanges([new(span, value)]); Load(document.EditorText); version = document.Version;
        SetSourceCaret(document, span.Start + value.Length, span.Start + value.Length); desiredSourceX = -1;
        sourceCaretChanged = Environment.TickCount64;
    }
    private int SourceLineHome(int row, int position)
    {
        var column = 0;
        while (column < lines[row].Length && char.IsWhiteSpace(lines[row][column])) column++;
        var first = lineStarts[row] + column;
        return position == first || column == lines[row].Length ? lineStarts[row] : first;
    }
    private static int PreviousSourceCodepoint(string text, int position)
    {
        if (position <= 0) return 0;
        var previous = position - 1;
        if (previous > 0 && char.IsLowSurrogate(text[previous]) && char.IsHighSurrogate(text[previous - 1])) previous--;
        return previous;
    }
    private int SourceWord(string value, int position, bool forward)
    {
        while (forward ? position < value.Length : position > 0)
        {
            var next = forward ? NextSourcePosition(position, value.Length) : PreviousSourcePosition(position);
            if (next == position || next == 0 || next == value.Length) return next;
            var before = SourceCharacterClass(value, PreviousSourcePosition(next)); var after = SourceCharacterClass(value, next);
            if (before != after && after is not (1 or 3 or 4)) return next;
            position = next;
        }
        return position;
    }
    private (int Start, int End) SourceWordRange(string value, int position)
    {
        var row = SourceRow(position); var lineStart = lineStarts[row]; var lineEnd = lineStart + lines[row].Length;
        if (position >= lineEnd && lineEnd > lineStart) position = PreviousSourcePosition(lineEnd);
        var start = position; var end = position; var kind = SourceCharacterClass(value, position);
        if (kind == 0) return (start, Math.Min(lineEnd, NextSourcePosition(start, value.Length)));
        while (start > lineStart && SourceCharacterClass(value, PreviousSourcePosition(start)) == kind) start = PreviousSourcePosition(start);
        while (end < lineEnd && SourceCharacterClass(value, end) == kind) end = NextSourcePosition(end, value.Length);
        return (start, end);
    }
    private (int Start, int End) SourceLineRange(int row) => (lineStarts[row], lineStarts[row] + lines[row].Length);
    private static int SourceCharacterClass(string value, int position)
    {
        if (position >= value.Length) return -1;
        if (value[position] is '\n' or '\r') return 4;
        if (value[position] == '_') return 2;
        if (!Rune.TryGetRuneAt(value, position, out var rune)) return 0;
        return Rune.GetUnicodeCategory(rune) switch
        {
            System.Globalization.UnicodeCategory.UppercaseLetter or System.Globalization.UnicodeCategory.LowercaseLetter
                or System.Globalization.UnicodeCategory.TitlecaseLetter or System.Globalization.UnicodeCategory.ModifierLetter
                or System.Globalization.UnicodeCategory.OtherLetter or System.Globalization.UnicodeCategory.DecimalDigitNumber => 2,
            System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark or System.Globalization.UnicodeCategory.EnclosingMark => 3,
            System.Globalization.UnicodeCategory.SpaceSeparator or System.Globalization.UnicodeCategory.LineSeparator
                or System.Globalization.UnicodeCategory.ParagraphSeparator or System.Globalization.UnicodeCategory.Control => 1,
            _ => 0,
        };
    }
    private void DrawNativeSourceSelection(ScriptSourceDocument document, ImDrawListPtr draw, Vector2 origin, Vector2 min, Vector2 max, float height)
    {
        if (rectangle is { } box)
        {
            var left = Math.Min(box.AnchorColumn, box.EndColumn) * MeasureGlyph(" "); var right = Math.Max(box.AnchorColumn, box.EndColumn) * MeasureGlyph(" ");
            for (var row = Math.Max(Math.Min(box.AnchorLine, box.EndLine), (int)((min.Y - origin.Y) / height)); row <= Math.Min(Math.Max(box.AnchorLine, box.EndLine), lines.Length - 1) && origin.Y + row * height < max.Y; row++)
                draw.AddRectFilled(origin + new Vector2(left, row * height), origin + new Vector2(Math.Max(left + 1, right), (row + 1) * height), Gui.GetColorU32(ImGuiCol.TextSelectedBg));
            return;
        }
        if (selection.Length == 0) return;
        var startRow = SourceRow(selection.Start); var endRow = SourceRow(selection.End);
        for (var row = Math.Max(startRow, (int)((min.Y - origin.Y) / height)); row <= endRow && origin.Y + row * height < max.Y; row++)
        {
            var start = row == startRow ? selection.Start - lineStarts[row] : 0;
            var end = row == endRow ? selection.End - lineStarts[row] : lines[row].Length;
            foreach (var range in SourceRangeRectangles(row, start, end))
                draw.AddRectFilled(origin + new Vector2(range.Minimum.X, row * height), origin + new Vector2(range.Maximum.X, (row + 1) * height), Gui.GetColorU32(ImGuiCol.TextSelectedBg));
            if (row < endRow)
            {
                var x = SourceX(row, lines[row].Length);
                draw.AddRectFilled(origin + new Vector2(x, row * height), origin + new Vector2(x + MeasureGlyph(" "), (row + 1) * height), Gui.GetColorU32(ImGuiCol.TextSelectedBg));
            }
        }
    }
}
