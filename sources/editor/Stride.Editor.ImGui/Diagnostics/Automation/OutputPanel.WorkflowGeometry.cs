using System.Numerics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed unsafe partial class EditorReadonlyRichText
{
    internal object InspectCharacterGeometry(int position)
    {
        if (layout == null || presentationWindow.IsNull) return new { Available = false };
        var geometry = layout.Caret(Math.Clamp(position, 0, source.Length));
        var point = presentationWindow.Pos + presentationWindow.WindowPadding - presentationWindow.Scroll
            + geometry.Point + new Vector2(1, geometry.Height * .5f);
        var clip = presentationWindow.InnerClipRect;
        return new { Available = true, Frame = Gui.GetFrameCount(), presentationWindow.LastFrameActive,
            Character = position, SourceLength = source.Length, PointX = point.X, PointY = point.Y,
            ClipMinX = clip.Min.X, ClipMinY = clip.Min.Y, ClipMaxX = clip.Max.X, ClipMaxY = clip.Max.Y,
            ScrollX = presentationWindow.Scroll.X, ScrollY = presentationWindow.Scroll.Y,
            ScrollMaxX = presentationWindow.ScrollMax.X, ScrollMaxY = presentationWindow.ScrollMax.Y,
            GlyphX = geometry.Point.X, GlyphY = geometry.Point.Y, GlyphHeight = geometry.Height,
            Visible = InspectRenderedCharacterPoint(position) != null };
    }
    internal Vector2? InspectRenderedCharacterPoint(int sourcePosition)
    {
        if (layout == null || presentationWindow.IsNull || Gui.GetFrameCount() - presentationWindow.LastFrameActive is not (0 or 1)) return null;
        var geometry = layout.Caret(Math.Clamp(sourcePosition, 0, source.Length));
        var point = presentationWindow.Pos + presentationWindow.WindowPadding - presentationWindow.Scroll
            + geometry.Point + new Vector2(1, geometry.Height * .5f);
        var clip = presentationWindow.InnerClipRect;
        return point.X >= clip.Min.X && point.X < clip.Max.X && point.Y >= clip.Min.Y && point.Y < clip.Max.Y ? point : null;
    }
}

internal sealed unsafe partial class EditorSelectableText
{
    internal object InspectSourceByteGeometry(int position) => new
    { Native = nativeLastDraw, Byte = position, Character = NativeCharacterOffset(position),
        Geometry = nativeTextView?.InspectCharacterGeometry(NativeCharacterOffset(position)) };
    internal Vector2? InspectRenderedSourceBytePoint(int sourceByte)
        => nativeLastDraw ? nativeTextView?.InspectRenderedCharacterPoint(NativeCharacterOffset(sourceByte)) : null;
}

internal sealed partial class OutputPanel
{
    internal object InspectWorkflowDiagnosticGeometry(string marker)
    {
        var matchByte = outputMatchIndex >= 0 && outputMatchIndex < outputMatches.Count
            ? outputMatches[outputMatchIndex].StartByte : -1;
        return new { outputMatchIndex, MatchCount = outputMatches.Count, MatchByte = matchByte,
            MarkerPosition = outputText.IndexOf(marker, StringComparison.Ordinal),
            Geometry = matchByte < 0 ? null : outputTextView.InspectSourceByteGeometry(matchByte) };
    }
    internal (BuildDiagnostic Diagnostic, Vector2? Point)? InspectWorkflowDiagnostic(string marker)
    {
        var entry = outputEntries.FirstOrDefault(entry => entry.Channel == outputChannel && entry.Diagnostic != null
            && entry.Text.Contains(marker, StringComparison.Ordinal));
        if (entry?.Diagnostic == null) return null;
        if (outputMatchIndex >= 0 && outputMatchIndex < outputMatches.Count && outputSearch == marker)
        {
            var matchByte = outputMatches[outputMatchIndex].StartByte;
            var character = System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(outputText), 0, matchByte).Length;
            var lineStart = character == 0 ? 0 : outputText.LastIndexOf('\n', character - 1) + 1;
            // The actual diagnostic row is clickable across its text. Its file
            // prefix remains visible even when the message is far to the right.
            return (entry.Diagnostic, outputTextView.InspectRenderedSourceBytePoint(System.Text.Encoding.UTF8.GetByteCount(outputText.AsSpan(0, lineStart))));
        }
        var position = outputText.IndexOf(marker, StringComparison.Ordinal);
        return position < 0 ? (entry.Diagnostic, null)
            : (entry.Diagnostic, outputTextView.InspectRenderedSourceBytePoint(System.Text.Encoding.UTF8.GetByteCount(outputText.AsSpan(0, position))));
    }
}

internal sealed partial class ScriptTextEditor
{
    internal (string Path, int Line, int Column)? InspectWorkflowCaret(string text)
    {
        if (navigation != null || string.IsNullOrEmpty(sourcePath)) return null;
        var position = Math.Clamp(caretPosition, 0, text.Length);
        var line = 1; var lineStart = 0;
        for (var index = 0; index < position; index++)
            if (text[index] == '\n') { line++; lineStart = index + 1; }
        return (sourcePath, line, position - lineStart + 1);
    }
}

internal sealed partial class EditorGame
{
    internal unsafe float WorkflowOutputHeight()
    {
        var window = ImGuiP.FindWindowByName(EditorPanels.Output.DisplayWindowName);
        return window.IsNull ? 0 : window.Size.Y;
    }
    internal unsafe (Vector2 Start, Vector2 End)? InspectWorkflowOutputSplitter()
    {
        var window = ImGuiP.FindWindowByName(EditorPanels.Output.DisplayWindowName);
        if (window.IsNull || window.DockNode.IsNull) return null;
        for (var node = window.DockNode; !node.ParentNode.IsNull; node = node.ParentNode)
        {
            var parent = node.ParentNode;
            if (parent.SplitAxis != ImGuiAxis.Y) continue;
            var first = new ImGuiDockNodePtr(parent.Handle->ChildNodes_0);
            var second = new ImGuiDockNodePtr((&parent.Handle->ChildNodes_0)[1]);
            if (first.IsNull || second.IsNull || second.Pos.Y <= first.Pos.Y) continue;
            var x = parent.Pos.X + parent.Size.X * .5f;
            var y = (first.Pos.Y + first.Size.Y + second.Pos.Y) * .5f;
            return (new Vector2(x, y), new Vector2(x, parent.Pos.Y + parent.Size.Y * .35f));
        }
        return null;
    }
    internal unsafe object InspectWorkflowOutputGeometry(string marker)
    {
        var window = ImGuiP.FindWindowByName(EditorPanels.Output.DisplayWindowName);
        return new { Diagnostic = outputPanel.InspectWorkflowDiagnosticGeometry(marker),
            Parent = window.IsNull ? null : new { window.LastFrameActive, ScrollY = window.Scroll.Y,
                ScrollMaxY = window.ScrollMax.Y, ClipMinX = window.InnerClipRect.Min.X,
                ClipMinY = window.InnerClipRect.Min.Y, ClipMaxX = window.InnerClipRect.Max.X,
                ClipMaxY = window.InnerClipRect.Max.Y } };
    }
    internal (BuildDiagnostic Diagnostic, Vector2? Point)? InspectWorkflowOutputDiagnostic(string marker)
        => outputPanel.InspectWorkflowDiagnostic(marker);
    internal (string Path, int Line, int Column)? InspectWorkflowActiveScriptCaret()
        => documents.Active?.Document is ScriptSourceDocument source && scriptEditors.TryGetValue(source, out var editor)
            ? editor.InspectWorkflowCaret(source.EditorText) : null;
}
