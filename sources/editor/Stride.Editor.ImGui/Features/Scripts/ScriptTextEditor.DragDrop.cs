using System.Numerics;
using System.Text;
using Hexa.NET.ImGui;
using Microsoft.CodeAnalysis.Text;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed unsafe partial class ScriptTextEditor
{
    private sealed class SourceDrag : IDisposable
    {
        internal readonly ScriptSourceDocument Document;
        internal readonly string Path, RawText;
        internal readonly long Version;
        internal readonly byte[]? Hash;
        internal readonly TextSpan[] Spans;
        internal readonly WindowsSourceTextDragDrop.Data Data;
        internal readonly bool CanMove;
        internal bool InternallyApplied;
        private readonly IDisposable? lease;
        internal SourceDrag(ScriptSourceDocument document, TextSpan[] spans, string value, bool rectangular, byte[]? html, bool authorizedMove)
        {
            Document = document; Path = document.Path; RawText = document.Text; Version = document.Version;
            Hash = document.DiskContentHash.IsEmpty ? null : document.DiskContentHash.ToArray(); Spans = spans;
            CanMove = authorizedMove && document.CanEditText && (!File.Exists(Path) || (File.GetAttributes(Path) & FileAttributes.ReadOnly) == 0); lease = document.AcquireSourceTransferLease();
            Data = new(value, rectangular, html, this);
        }
        internal void Validate(bool editing)
        {
            if (editing && !CanMove) throw new InvalidOperationException("The source drag is read-only.");
            Document.ValidateSourceTransfer(Path, Version, RawText, Hash, editing);
        }
        public void Dispose() => lease?.Dispose();
    }
    private sealed class DragLayout : IDisposable
    {
        internal readonly string[] Lines;
        internal readonly int[] Starts;
        internal readonly long Version;
        internal readonly nint Font;
        internal readonly float Size, Dpi, Space;
        private readonly Dictionary<int, SourceLineGeometry> rows;
        private readonly Dictionary<string, float> widths;
        internal readonly WindowsTextLayout.GlyphRasterizer Rasterizer;
        internal DragLayout(ScriptTextEditor editor, long version)
        {
            Lines = editor.lines; Starts = editor.lineStarts; Version = version;
            Font = editor.geometryFont; Size = editor.geometryFontSize; Dpi = editor.geometryDpi;
            rows = []; widths = new(editor.glyphWidths, StringComparer.Ordinal); Rasterizer = editor.nativeGlyphRasterizer!;
            Space = Math.Max(1, Measure(" "));
        }
        private float Measure(string value)
        {
            if (widths.TryGetValue(value, out var width)) return width;
            using var metric = new WindowsTextLayout(value, "Consolas", Size / Dpi, 1_000_000);
            width = metric.Metrics.WidthIncludingTrailingWhitespace * Dpi;
            widths.Add(value, width); return width;
        }
        internal SourceLineGeometry Row(int row)
        {
            if (!rows.TryGetValue(row, out var layout))
            {
                if (rows.Count >= 256) { var old = rows.First(); old.Value.Dispose(); rows.Remove(old.Key); }
                rows.Add(row, layout = BuildSourceGeometry(Lines[row], Size, Dpi, Rasterizer));
            }
            return layout;
        }
        public void Dispose() { foreach (var row in rows.Values) row.Dispose(); rows.Clear(); }
    }
    private sealed record DropSurface(ScriptSourceDocument Document, string Path, string RawText, byte[]? Hash,
        DragLayout Layout, Vector2 Min, Vector2 Max, Vector2 Origin, float Height);
    private WindowsSourceTextDragDrop.Binding? sourceDropBinding;
    private nint sourceDropWindow;
    private DropSurface? dropSurface;
    private DragLayout? dragLayout;
    private SourceDrag? queuedNativeDrag;
    private bool possibleSourceDrag;
    private Vector2 possibleSourceDragPoint;
    private int possibleSourceDragPosition;
    private SourceRectangle? queuedDropRectangle;
    internal Func<bool>? CanTransferNativeText { get; set; }
    internal Action? NativeModalTick { get; set; }
    private (int Position, float X)? nativeDropCursor;
    private Vector2 nativeDropScroll;
    private WindowsSourceTextDragDrop.Data? lastNativeData;
    private string normalizedNativeData = "";

    private void ObserveSourceDropSurface(ScriptSourceDocument document, Vector2 origin, Vector2 min, Vector2 max, float height)
    {
        var viewport = Gui.GetWindowViewport(); var window = (nint)viewport.PlatformHandleRaw;
        if (window == 0) return;
        WindowsSourceTextDragDrop.Frame = Gui.GetFrameCount();
        if (sourceDropWindow != window)
        {
            sourceDropBinding?.Dispose(); sourceDropBinding = WindowsSourceTextDragDrop.Attach(window); sourceDropWindow = window;
            sourceDropBinding.Transfer = TransferNativeSource;
        }
        sourceDropBinding!.SeenFrame = Gui.GetFrameCount();
        sourceDropBinding.Transfer = TransferNativeSource;
        sourceDropBinding.ModalTick = NativeModalTick;
        sourceDropBinding.ClearHover = () => { nativeDropCursor = null; nativeDropScroll = Vector2.Zero; };
        if (dragLayout == null || dragLayout.Version != document.Version || dragLayout.Font != geometryFont || dragLayout.Size != geometryFontSize || dragLayout.Dpi != geometryDpi)
        { dragLayout?.Dispose(); dragLayout = new(this, document.Version); }
        var offset = WindowsSourceTextDragDrop.ClientOrigin(window) - viewport.Pos;
        dropSurface = new(document, document.Path, document.Text, document.DiskContentHash.IsEmpty ? null : document.DiskContentHash.ToArray(),
            dragLayout, min + offset, max + offset, origin + offset, height);
    }
    private bool IsSourceSelectionHit(int row, int position, float x)
    {
        if (rectangle is { } box)
            return row >= Math.Min(box.AnchorLine, box.EndLine) && row <= Math.Max(box.AnchorLine, box.EndLine)
                && x >= Math.Min(box.AnchorColumn, box.EndColumn) * MeasureGlyph(" ") && x <= Math.Max(box.AnchorColumn, box.EndColumn) * MeasureGlyph(" ")
                && RectangleSegments().Any(segment => segment.Span.Length > 0);
        if (selection.Length == 0) return false;
        var start = Math.Max(0, selection.Start - lineStarts[row]); var end = Math.Min(lines[row].Length, selection.End - lineStarts[row]);
        return end > start && SourceRangeRectangles(row, start, end).Any(range => x >= range.Minimum.X && x <= range.Maximum.X);
    }
    private void ContinueSourceDrag(ScriptSourceDocument document)
    {
        if (!possibleSourceDrag) return;
        if (!ImGuiP.IsMouseDown(ImGuiMouseButton.Left, sourceSurfaceId))
        {
            possibleSourceDrag = false; rectangle = null; SetSourceCaret(document, possibleSourceDragPosition, possibleSourceDragPosition); return;
        }
        if (!WindowsSourceTextDragDrop.ExceedsThreshold(Gui.GetMousePos() - possibleSourceDragPoint)) return;
        possibleSourceDrag = false;
        if (rectangle != null)
        {
            var segments = RectangleSegments();
            queuedNativeDrag = new(document, segments.Where(segment => segment.Span.Length > 0).Select(segment => segment.Span).ToArray(),
                string.Join(Environment.NewLine, segments.GroupBy(segment => segment.Line)
                    .Select(row => string.Concat(row.Select(segment => document.CopySourceRange(segment.Span.Start, segment.Span.Length))))), true, null, MayEditSource(document));
        }
        else queuedNativeDrag = new(document, [selection], document.CopySourceRange(selection.Start, selection.Length), false, SourceClipboardHtml(document, selection), MayEditSource(document));
        ReleaseNativeSourceFocus(); sourceDragging = false;
    }
    internal void PumpNativeSourceDrag()
    {
        if (queuedNativeDrag is not { } request) return;
        queuedNativeDrag = null;
        try
        {
            using (request)
            {
                if (CanTransferNativeText?.Invoke() == false || sourceDropBinding?.SeenFrame != WindowsSourceTextDragDrop.Frame) return;
                request.Validate(false);
                var effects = WindowsSourceTextDragDrop.Copy | (request.CanMove && MayEditSource(request.Document) ? WindowsSourceTextDragDrop.Move : 0);
                var effect = WindowsSourceTextDragDrop.Start(sourceDropWindow, request.Data, effects);
                if (effect == WindowsSourceTextDragDrop.Move && !request.InternallyApplied)
                {
                    if (CanTransferNativeText?.Invoke() == false) throw new InvalidOperationException("Source transfer is no longer available.");
                    request.Validate(true);
                    using var transaction = request.Document.SessionHistory?.CreateTransaction("Move source text");
                    request.Document.UpdateEditorChanges(request.Spans.Select(span => new TextChange(span, "")));
                    QueueSourceDropSelection(request.Document, request.Spans[0].Start, request.Spans[0].Start, null);
                }
            }
        }
        finally { if (Gui.GetCurrentContext().Handle != null) Gui.GetIO().AddMouseButtonEvent(0, false); }
    }
    private uint TransferNativeSource(WindowsSourceTextDragDrop.Data data, Vector2 screen, uint keys, uint allowed, bool drop)
    {
        if (CanTransferNativeText?.Invoke() == false || dropSurface is not { } surface || screen.X < surface.Min.X || screen.Y < surface.Min.Y || screen.X > surface.Max.X || screen.Y > surface.Max.Y
            || !MayEditSource(surface.Document) || surface.Document.Path != surface.Path || surface.Document.Version != surface.Layout.Version) return 0;
        var target = surface.Document; var layout = surface.Layout; var point = screen - surface.Origin;
        var row = Math.Clamp((int)MathF.Floor(point.Y / surface.Height), 0, layout.Lines.Length - 1); var x = Math.Max(0, point.X);
        var position = layout.Starts[row] + layout.Row(row).Hit(x);
        var origin = data.Origin as SourceDrag;
        if (origin != null && data.Text != origin.Data.Text) return 0;
        var effect = (keys & 8) == 0 && (allowed & WindowsSourceTextDragDrop.Move) != 0 ? WindowsSourceTextDragDrop.Move : WindowsSourceTextDragDrop.Copy;
        if (origin != null && (!origin.CanMove || !ReferenceEquals(origin.Document.SessionHistory, target.SessionHistory)
            || !ReferenceEquals(origin.Document, target) && target.SessionHistory == null)) effect = WindowsSourceTextDragDrop.Copy;
        effect &= allowed; if (effect == 0) return 0;
        if (!ReferenceEquals(lastNativeData, data))
        { lastNativeData = data; normalizedNativeData = data.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\t", "    ", StringComparison.Ordinal); }
        var value = normalizedNativeData;
        if (value.Length == 0) return 0;
        var inserted = new List<TextChange>(); var rectangular = false; var endRow = row;
        if (data.Rectangle)
        {
            var values = value.Split('\n'); endRow = row + values.Length - 1;
            if (endRow < layout.Lines.Length)
            {
                rectangular = true;
                for (var index = 0; index < values.Length; index++)
                {
                    var line = row + index; var geometry = layout.Row(line); var offset = layout.Starts[line] + geometry.Hit(x);
                    var padding = Math.Max(0, (int)MathF.Round((x - geometry.Width) / layout.Space));
                    inserted.Add(new(new(offset, 0), values[index].Length > 0 ? new string(' ', padding) + values[index] : ""));
                }
            }
        }
        if (!rectangular) inserted.Add(new(new(position, 0), value));
        if (origin != null && ReferenceEquals(origin.Document, target) && inserted.Any(change => origin.Spans.Any(span => change.Span.Start >= span.Start && change.Span.Start <= span.End))) return 0;
        if (!drop)
        {
            nativeDropCursor = (position, rectangular ? x : layout.Row(row).Native.HitCaret(new(x, surface.Height / 2)).X);
            nativeDropScroll = new(screen.X < surface.Min.X + surface.Height ? -surface.Height : screen.X > surface.Max.X - surface.Height ? surface.Height : 0,
                screen.Y < surface.Min.Y + surface.Height ? -surface.Height : screen.Y > surface.Max.Y - surface.Height ? surface.Height : 0);
            return effect;
        }
        using var targetLease = target.AcquireSourceTransferLease();
        target.ValidateSourceTransfer(surface.Path, layout.Version, surface.RawText, surface.Hash, true);
        if (origin != null) origin.Validate(effect == WindowsSourceTextDragDrop.Move);
        var removed = effect == WindowsSourceTextDragDrop.Move && origin != null ? origin.Spans.Select(span => new TextChange(span, "")).ToArray() : [];
        var same = origin != null && ReferenceEquals(origin.Document, target);
        var targetChanges = same ? inserted.Concat(removed).OrderBy(change => change.Span.Start).ToArray() : inserted.ToArray();
        void Apply()
        {
            if (!MayEditSource(target) || CanTransferNativeText?.Invoke() == false)
                throw new InvalidOperationException("The source drop target is no longer editable.");
            target.ValidateSourceTransfer(surface.Path, layout.Version, surface.RawText, surface.Hash, true);
            if (origin != null) origin.Validate(removed.Length > 0);
            target.UpdateEditorChanges(targetChanges);
            if (!same && removed.Length > 0) origin!.Document.UpdateEditorChanges(removed);
            if (origin != null) origin.InternallyApplied = true;
        }
        if (target.SessionHistory is { } history)
        {
            using var capture = history.CaptureOperationsAtomic(Apply); capture.Publish(history, effect == WindowsSourceTextDragDrop.Move ? "Move source text" : "Copy source text");
        }
        else
        {
            // Independent non-session documents cannot participate in an internal cross-owner move.
            if (!same && removed.Length > 0) throw new InvalidOperationException("The source documents do not share canonical authoring history.");
            Apply();
        }
        var first = inserted[0]; var start = MapSourcePosition(first.Span.Start, targetChanges, true);
        var end = start + (first.NewText?.Length ?? 0);
        SourceRectangle? box = null;
        if (rectangular)
        {
            var after = target.EditorText.Split('\n'); var firstRow = SourceText.From(target.EditorText).Lines.GetLineFromPosition(start).LineNumber;
            using var updated = BuildSourceGeometry(after[firstRow], layout.Size, layout.Dpi, layout.Rasterizer);
            var column = end - SourceText.From(target.EditorText).Lines[firstRow].Start;
            box = new(firstRow, x / layout.Space, Math.Clamp(firstRow + endRow - row, 0, after.Length - 1), updated.X(column) / layout.Space);
        }
        QueueSourceDropSelection(target, start, end, box);
        return effect;
    }
    private void QueueSourceDropSelection(ScriptSourceDocument document, int start, int end, SourceRectangle? box)
    {
        var text = document.EditorText; start = Math.Clamp(start, 0, text.Length); end = Math.Clamp(end, start, text.Length);
        navigation = null; pendingCursor = null;
        pendingSelection = (Encoding.UTF8.GetByteCount(text.AsSpan(0, start)), Encoding.UTF8.GetByteCount(text.AsSpan(0, end)));
        pendingSelectionCaretAtEnd = true; queuedDropRectangle = box; focusSource = true; sourceReveal = true;
        DismissAssistance();
    }
    private void ReleaseSourceDrop()
    {
        sourceDropBinding?.Dispose(); sourceDropBinding = null; sourceDropWindow = 0; dropSurface = null; dragLayout?.Dispose(); dragLayout = null;
        queuedNativeDrag?.Dispose(); queuedNativeDrag = null; possibleSourceDrag = false;
        nativeDropCursor = null; lastNativeData = null; normalizedNativeData = "";
    }
}
