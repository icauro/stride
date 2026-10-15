using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using Stride.Graphics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal readonly record struct EditorRichTextHeading(int Start, int Length, int Level);
internal readonly record struct EditorRichTextLink(int Start, int Length, string Address);
internal readonly record struct EditorRichTextHighlight(int Start, int Length);

// Native wrapped layout and source offsets remain one immutable presentation snapshot.
internal sealed unsafe partial class EditorReadonlyRichText : IDisposable
{
    internal WindowsGlyphImages? GlyphImages { get; set; }
    internal CommandList? GlyphCommands { get; set; }
    internal string FontFamily { get; set; } = "Segoe UI";
    internal bool CopyWholeWhenEmpty { get; set; }
    internal bool ShowSelectAllMenu { get; set; } = true;
    internal float? FollowTailToleranceDips { get; set; }
    internal bool FollowTailWithSelection { get; set; }
    internal IReadOnlyList<WindowsTextLayout.ColorRange> TextColors { get; set; } = Array.Empty<WindowsTextLayout.ColorRange>();
    private WindowsTextLayout.ColorRange[] layoutTextColors = [];
    internal bool Active => focused;
    internal bool Hovered { get; private set; }
    internal bool OwnedMouseDoubleClicked => surfaceId != 0 && ImGuiP.IsMouseDoubleClicked(ImGuiMouseButton.Left, surfaceId);
    internal int SelectionStart => anchor;
    internal int SelectionEnd => caret;
    internal int Caret => caret;
    private WindowsFormattedText? layout;
    private WindowsTextLayout.GlyphRasterizer? rasterizer;
    private string source = "", layoutFamily = "";
    private string layoutLocale = "";
    private float layoutWidth = -1, layoutSize = -1, layoutDpi = -1;
    private uint layoutColor, layoutLinkColor;
    private bool layoutWrap;
    private EditorRichTextHeading[] layoutHeadings = [];
    private EditorRichTextLink[] layoutLinks = [];
    private sealed record LinkRegion(string Address, (Vector2 Minimum, Vector2 Maximum)[] Ranges);
    private LinkRegion[] linkRegions = [];
    private int paintedStart = -1, paintedEnd = -1;
    private (Vector2 Minimum, Vector2 Maximum)[] selectedRanges = [];
    private EditorRichTextHighlight[] paintedHighlights = [];
    private (Vector2 Minimum, Vector2 Maximum)[][] highlightRanges = [];
    private int[] elements = [];
    private int anchor, caret;
    private bool focused, dragging;
    private uint surfaceId;
    private ImGuiWindowPtr presentationWindow;
    private Vector2 pressPosition;
    private string? pressedLink;
    private (int Position, Vector2 Point, float Height)? visualCaret;
    private (int Start, int End, bool Focus)? pendingSelection;
    private bool revealSelection;

    internal void Select(int start, int end, bool focus = false) => pendingSelection = (start, end, focus);
    internal void Suspend()
    {
        focused = dragging = Hovered = false; pressedLink = null;
        if (Gui.GetCurrentContext().Handle != null && surfaceId != 0 && ImGuiP.GetActiveID() == surfaceId) ImGuiP.ClearActiveID();
    }
    public void Draw(string id, string text, IReadOnlyList<EditorRichTextHeading> headings,
        IReadOnlyList<EditorRichTextLink> links, Vector2 size, ImFontPtr boldFont,
        Action<string> openLink, bool expandHeightToContent, bool wrap = true, bool followTail = false,
        IReadOnlyList<EditorRichTextHighlight>? highlights = null, bool autoHeight = false)
    {
        Hovered = false;
        if (GlyphImages == null || GlyphCommands == null || GlyphImages.IsDraining) { Suspend(); return; }
        var fontSize = Gui.GetFontSize(); var dpi = Gui.GetStyle().FontScaleDpi;
        var tolerance = FollowTailToleranceDips is { } dips ? dips * dpi : fontSize;
        var tailBefore = presentationWindow.IsNull || presentationWindow.ScrollMax.Y - presentationWindow.Scroll.Y < tolerance;
        var selectedBefore = anchor != caret;
        Gui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(fontSize / 6, 0));
        try
        {
            var width = Math.Max(1, (size.X > 0 ? size.X : Gui.GetContentRegionAvail().X) - Gui.GetStyle().WindowPadding.X * 2 - (wrap ? Gui.GetStyle().ScrollbarSize : 0));
            var changed = source != text || layoutWidth != width || layoutSize != fontSize || layoutDpi != dpi || layoutFamily != FontFamily || layoutLocale != CultureInfo.CurrentUICulture.Name
                || layoutWrap != wrap || layoutColor != Gui.GetColorU32(ImGuiCol.Text) || layoutLinkColor != Gui.GetColorU32(ImGuiCol.TextLink)
                || !Same(layoutHeadings, headings) || !Same(layoutLinks, links) || !Same(layoutTextColors, TextColors);
            if (changed) Layout(text, headings, links, width, fontSize, dpi, wrap);
            if (autoHeight) size.Y = Math.Max(1, layout!.HeightForLines(8) + Gui.GetStyle().FramePadding.Y * 2 + 4 * dpi);
            if (expandHeightToContent) size.Y = Math.Max(1, layout!.Height + Gui.GetStyle().WindowPadding.Y * 2);
            Gui.SetNextWindowContentSize(new(wrap ? width : Math.Max(width, layout!.Width), layout!.Height));
            using var child = ImGuiScopes.Child(id, size, ImGuiChildFlags.AlwaysUseWindowPadding,
                wrap ? ImGuiWindowFlags.None : ImGuiWindowFlags.HorizontalScrollbar);
            if (child.DrawContents)
            {
                var window = ImGuiP.GetCurrentWindow(); var clip = window.InnerClipRect;
                presentationWindow = window;
                if (followTail && changed && tailBefore && (!selectedBefore || FollowTailWithSelection)) ImGuiP.SetScrollY(window, Math.Max(0, layout.Height));
                var origin = window.Pos + window.WindowPadding - window.Scroll;
                Gui.SetCursorScreenPos(clip.Min);
                Gui.InvisibleButton("##flow", Vector2.Max(Vector2.One, clip.Max - clip.Min), ImGuiButtonFlags.EnableNav | ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
                surfaceId = Gui.GetItemID();
                if (pendingSelection is { } requested)
                {
                    anchor = Math.Clamp(requested.Start, 0, source.Length); caret = Math.Clamp(requested.End, 0, source.Length);
                    if (requested.Focus) { focused = true; ImGuiP.FocusWindow(window); ImGuiP.SetFocusID(surfaceId, window); ImGuiP.SetActiveID(surfaceId, window); }
                    pendingSelection = null; visualCaret = null; revealSelection = true;
                }
                var hovered = Hovered = Gui.IsItemHovered(); var io = Gui.GetIO(); var point = io.MousePos - origin;
                var currentCaret = CaretGeometry();
                var hit = hovered || dragging ? layout.Hit(point) : (Position: caret, Point: currentCaret.Point, Height: currentCaret.Height);
                var link = hovered ? LinkAt(point) : null;
                if (hovered) Gui.SetMouseCursor(link == null ? ImGuiMouseCursor.TextInput : ImGuiMouseCursor.Hand);
                if (hovered && ImGuiP.IsMouseClicked(ImGuiMouseButton.Left, ImGuiInputFlags.None, surfaceId))
                {
                    focused = dragging = true; pressPosition = io.MousePos; pressedLink = link;
                    ImGuiP.FocusWindow(window); ImGuiP.SetFocusID(surfaceId, window); ImGuiP.SetActiveID(surfaceId, window);
                    if (!io.KeyShift) anchor = hit.Position;
                    caret = hit.Position; visualCaret = hit;
                    if (Gui.GetMouseClickedCount(ImGuiMouseButton.Left) == 2) SelectWord(hit.Position);
                    else if (Gui.GetMouseClickedCount(ImGuiMouseButton.Left) == 3) SelectParagraph(hit.Position);
                    ImGuiP.SetKeyOwner(ImGuiKey.MouseLeft, surfaceId, (ImGuiInputFlags)ImGuiInputFlagsPrivate.LockUntilRelease);
                }
                else if (Gui.IsMouseClicked(ImGuiMouseButton.Left) && !hovered) focused = false;
                if (Gui.IsItemFocused()) focused = true;
                if (!Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) || ImGuiP.GetActiveID() != 0 && ImGuiP.GetActiveID() != surfaceId) focused = false;
                if (focused && ImGuiP.GetActiveID() == 0) ImGuiP.SetActiveID(surfaceId, window);
                if (dragging && ImGuiP.IsMouseDown(ImGuiMouseButton.Left, surfaceId))
                {
                    if (Vector2.DistanceSquared(pressPosition, io.MousePos) > 9) { caret = hit.Position; visualCaret = hit; }
                    if (io.MousePos.Y < clip.Min.Y) ImGuiP.SetScrollY(window, Math.Max(0, window.Scroll.Y - (clip.Min.Y - io.MousePos.Y) * io.DeltaTime * 12));
                    else if (io.MousePos.Y > clip.Max.Y) ImGuiP.SetScrollY(window, window.Scroll.Y + (io.MousePos.Y - clip.Max.Y) * io.DeltaTime * 12);
                }
                if (dragging && ImGuiP.IsMouseReleased(ImGuiMouseButton.Left, surfaceId))
                {
                    dragging = false;
                    if (pressedLink != null && pressedLink == link && anchor == caret && Vector2.DistanceSquared(pressPosition, io.MousePos) <= 9) openLink(pressedLink);
                    pressedLink = null;
                }
                if (focused) Keyboard(clip.Max.Y - clip.Min.Y);
                if (revealSelection)
                {
                    var target = CaretGeometry();
                    if (target.Point.Y < window.Scroll.Y) ImGuiP.SetScrollY(window, target.Point.Y);
                    else if (target.Point.Y + target.Height > window.Scroll.Y + clip.Max.Y - clip.Min.Y) ImGuiP.SetScrollY(window, target.Point.Y + target.Height - clip.Max.Y + clip.Min.Y);
                    revealSelection = false;
                }
                if (Gui.BeginPopupContextItem("##flowCopy"))
                {
                    try
                    {
                        if (Gui.MenuItem(EditorLocalization.Widget("Copy", "Menu"), "Ctrl+C", false, anchor != caret || CopyWholeWhenEmpty && source.Length > 0)) Copy();
                        if (ShowSelectAllMenu && Gui.MenuItem("Select all", "Ctrl+A")) { anchor = 0; caret = source.Length; visualCaret = null; }
                    }
                    finally { Gui.EndPopup(); }
                }
                Paint(window.DrawList, origin, clip.Min, clip.Max, highlights);
            }
            else Suspend();
        }
        finally { Gui.PopStyleVar(); }
    }
    private static bool Same<T>(T[] before, IReadOnlyList<T> after) where T : IEquatable<T>
    {
        if (before.Length != after.Count) return false;
        for (var index = 0; index < before.Length; index++) if (!before[index].Equals(after[index])) return false;
        return true;
    }
    private void Layout(string text, IReadOnlyList<EditorRichTextHeading> headings, IReadOnlyList<EditorRichTextLink> links, float width, float fontSize, float dpi, bool wrap)
    {
        var styles = new List<WindowsTextLayout.StyleRange>(); var colors = new List<WindowsTextLayout.ColorRange>(); var underlines = new List<(int, int)>();
        var nextTextColors = TextColors.ToArray();
        foreach (var range in nextTextColors)
        {
            if (range.Start < 0 || range.Length < 0 || range.Start > text.Length - range.Length)
                throw new ArgumentOutOfRangeException(nameof(TextColors));
            if (range.Length > 0) colors.Add(range);
        }
        foreach (var heading in headings)
        {
            if (heading.Length == 0) continue;
            var factor = heading.Level switch { 1 => 2f, 2 => 1.5f, 3 => 1.17f, _ => 1f };
            styles.Add(new(heading.Start, heading.Length, fontSize / dpi * factor, heading.Level is >= 1 and <= 3 ? 700u : heading.Level == 4 ? 300u : 400u));
        }
        var linkColor = Gui.GetColorU32(ImGuiCol.TextLink);
        foreach (var link in links)
        {
            if (link.Length == 0) continue;
            colors.Add(new(link.Start, link.Length, linkColor)); underlines.Add((link.Start, link.Length));
        }
        rasterizer ??= new();
        var nextElements = StringInfo.ParseCombiningCharacters(text); var nextHeadings = headings.ToArray(); var nextLinks = links.ToArray();
        var prepared = new WindowsFormattedText(text, FontFamily, fontSize / dpi, wrap ? width : 1_000_000 * dpi, dpi, wrap,
            styles, colors.OrderBy(color => color.Start).ToArray(), underlines, rasterizer, Gui.GetColorU32(ImGuiCol.Text));
        LinkRegion[] nextRegions;
        try { nextRegions = nextLinks.Select(link => new LinkRegion(link.Address, prepared.Selection(link.Start, link.Length))).ToArray(); }
        catch { prepared.Dispose(); throw; }
        layout?.Dispose(); layout = prepared;
        if (source != text && !text.StartsWith(source, StringComparison.Ordinal)) anchor = caret = 0;
        source = text; layoutWidth = width; layoutSize = fontSize; layoutDpi = dpi; layoutFamily = FontFamily; layoutLocale = CultureInfo.CurrentUICulture.Name; layoutWrap = wrap;
        layoutColor = Gui.GetColorU32(ImGuiCol.Text); layoutLinkColor = linkColor;
        layoutHeadings = nextHeadings; layoutLinks = nextLinks; linkRegions = nextRegions; elements = nextElements; visualCaret = null;
        layoutTextColors = nextTextColors;
        paintedStart = paintedEnd = -1; selectedRanges = []; paintedHighlights = []; highlightRanges = [];
    }
    private string? LinkAt(Vector2 point)
    {
        foreach (var link in linkRegions)
            foreach (var range in link.Ranges)
                if (point.X >= range.Minimum.X && point.X < range.Maximum.X && point.Y >= range.Minimum.Y && point.Y < range.Maximum.Y) return link.Address;
        return null;
    }
    private void Paint(ImDrawListPtr draw, Vector2 origin, Vector2 clipMin, Vector2 clipMax, IReadOnlyList<EditorRichTextHighlight>? highlights)
    {
        draw.PushClipRect(clipMin, clipMax, true);
        try
        {
            if (!Same(paintedHighlights, highlights ?? Array.Empty<EditorRichTextHighlight>()))
            {
                var next = highlights?.ToArray() ?? [];
                var ranges = next.Select(highlight =>
                {
                    var start = Math.Clamp(highlight.Start, 0, source.Length);
                    return layout!.Selection(start, Math.Clamp(highlight.Length, 0, source.Length - start));
                }).ToArray();
                paintedHighlights = next; highlightRanges = ranges;
            }
            foreach (var ranges in highlightRanges)
                foreach (var range in ranges)
                    if (origin.Y + range.Maximum.Y >= clipMin.Y && origin.Y + range.Minimum.Y <= clipMax.Y)
                        draw.AddRectFilled(origin + range.Minimum, origin + range.Maximum, 0xffdec4b0);
            var low = Math.Min(anchor, caret); var high = Math.Max(anchor, caret);
            if (paintedStart != low || paintedEnd != high)
            { selectedRanges = layout!.Selection(low, high - low); paintedStart = low; paintedEnd = high; }
            foreach (var range in selectedRanges)
                if (origin.Y + range.Maximum.Y >= clipMin.Y && origin.Y + range.Minimum.Y <= clipMax.Y)
                    draw.AddRectFilled(origin + range.Minimum, origin + range.Maximum, Gui.GetColorU32(ImGuiCol.TextSelectedBg));
            layout!.Draw(draw, GlyphCommands!, GlyphImages!, origin, clipMin, clipMax);
            if (focused && anchor == caret && Gui.GetTime() % 1.2 < .8)
            {
                var current = CaretGeometry(); var point = origin + current.Point;
                draw.AddLine(point, point + new Vector2(0, current.Height), Gui.GetColorU32(ImGuiCol.Text));
            }
        }
        finally { draw.PopClipRect(); }
    }
    private (Vector2 Point, float Height) CaretGeometry()
        => visualCaret is { } visual && visual.Position == caret ? (visual.Point, visual.Height) : layout!.Caret(caret);
    private void Copy()
    {
        var low = Math.Min(anchor, caret); var high = Math.Max(anchor, caret);
        if (low == high && CopyWholeWhenEmpty) { low = 0; high = source.Length; }
        if (low != high) WindowsScriptClipboard.TryWrite((nint)Gui.GetWindowViewport().PlatformHandleRaw, source[low..high], false);
    }
    private void SelectWord(int position)
    {
        anchor = caret = Math.Clamp(position, 0, source.Length); visualCaret = null;
        while (anchor > 0 && !char.IsWhiteSpace(source[anchor - 1])) anchor = Previous(anchor);
        while (caret < source.Length && !char.IsWhiteSpace(source[caret])) caret = Next(caret);
    }
    private void SelectParagraph(int position)
    {
        anchor = caret = Math.Clamp(position, 0, source.Length); visualCaret = null;
        while (anchor > 0 && source[anchor - 1] is not ('\r' or '\n')) anchor--;
        while (caret < source.Length && source[caret] is not ('\r' or '\n')) caret++;
    }
    private int Previous(int position) { var index = Array.BinarySearch(elements, position); return index >= 0 ? index > 0 ? elements[index - 1] : 0 : ~index > 0 ? elements[~index - 1] : 0; }
    private int Next(int position) { var index = Array.BinarySearch(elements, position); index = index >= 0 ? index + 1 : ~index; return index < elements.Length ? elements[index] : source.Length; }
    private void Keyboard(float pageHeight)
    {
        foreach (var key in NavigationKeys) ImGuiP.SetKeyOwner(key, surfaceId);
        bool Press(ImGuiKey key) => ImGuiP.IsKeyPressed(key, ImGuiInputFlags.Repeat, surfaceId);
        var io = Gui.GetIO();
        if (io.KeyCtrl && Press(ImGuiKey.A)) { anchor = 0; caret = source.Length; visualCaret = null; }
        if (io.KeyCtrl && Press(ImGuiKey.C)) Copy();
        var next = caret; var current = CaretGeometry(); (int Position, Vector2 Point, float Height)? target = null;
        if (Press(ImGuiKey.LeftArrow))
        {
            if (!io.KeyShift && anchor != caret) next = Math.Min(anchor, caret);
            else if (!io.KeyCtrl) target = layout!.VisualMove(caret, current.Point, false);
            else next = Previous(caret);
            if (io.KeyCtrl) { while (next > 0 && char.IsWhiteSpace(source[next - 1])) next = Previous(next); while (next > 0 && !char.IsWhiteSpace(source[next - 1])) next = Previous(next); }
        }
        if (Press(ImGuiKey.RightArrow))
        {
            if (!io.KeyShift && anchor != caret) next = Math.Max(anchor, caret);
            else if (!io.KeyCtrl) target = layout!.VisualMove(caret, current.Point, true);
            else next = Next(caret);
            if (io.KeyCtrl) { while (next < source.Length && !char.IsWhiteSpace(source[next])) next = Next(next); while (next < source.Length && char.IsWhiteSpace(source[next])) next = Next(next); }
        }
        if (Press(ImGuiKey.Home)) { if (io.KeyCtrl) next = 0; else target = layout!.Hit(new(-1, current.Point.Y + current.Height / 2)); }
        if (Press(ImGuiKey.End)) { if (io.KeyCtrl) next = source.Length; else target = layout!.Hit(new(layout!.Width + layoutWidth + 1, current.Point.Y + current.Height / 2)); }
        if (Press(ImGuiKey.UpArrow)) target = layout!.Hit(current.Point + new Vector2(0, -current.Height / 2));
        if (Press(ImGuiKey.DownArrow)) target = layout!.Hit(current.Point + new Vector2(0, current.Height * 1.5f));
        if (Press(ImGuiKey.PageUp)) target = layout!.Hit(current.Point - new Vector2(0, pageHeight));
        if (Press(ImGuiKey.PageDown)) target = layout!.Hit(current.Point + new Vector2(0, pageHeight));
        if (target is { } visual) next = visual.Position;
        if (next != caret || target != null)
        {
            caret = next; if (!io.KeyShift) anchor = caret; visualCaret = target; revealSelection = true;
        }
    }
    private static readonly ImGuiKey[] NavigationKeys = [ImGuiKey.A, ImGuiKey.C, ImGuiKey.LeftArrow, ImGuiKey.RightArrow,
        ImGuiKey.UpArrow, ImGuiKey.DownArrow, ImGuiKey.Home, ImGuiKey.End, ImGuiKey.PageUp, ImGuiKey.PageDown];
    public void Dispose()
    {
        Suspend(); layout?.Dispose(); layout = null; rasterizer?.Dispose(); rasterizer = null; GlyphImages = null; GlyphCommands = null; presentationWindow = default;
    }
}
