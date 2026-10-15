using System.Globalization;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed unsafe partial class ScriptTextEditor
{
    private sealed record PreviewNativeLine(SourceLineGeometry Geometry, string Number)
    {
        internal int UsedFrame;
    }
    private readonly Dictionary<(string[] Text, int Line, uint Color, uint ControlColor), PreviewNativeLine> previewSourceGeometry = [];
    private float previewFontSize = -1, previewDpi = -1;
    private nint previewFont;
    private void ClearPreviewSourceGeometry()
    {
        foreach (var row in previewSourceGeometry.Values) row.Geometry.Dispose();
        previewSourceGeometry.Clear();
    }
    private void EnsurePreviewSourceGeometry()
    {
        if (NativeGlyphCommands == null || NativeGlyphImages == null) throw new InvalidOperationException("The source preview has no current native glyph graphics owner.");
        var font = (nint)Gui.GetFont().Handle;
        if (previewFontSize == Gui.GetFontSize() && previewDpi == Gui.GetStyle().FontScaleDpi && previewFont == font) return;
        ClearPreviewSourceGeometry(); previewFontSize = Gui.GetFontSize(); previewDpi = Gui.GetStyle().FontScaleDpi; previewFont = font;
    }
    private PreviewNativeLine PreviewSourceGeometry(string[] text, int line, uint color, uint controls)
    {
        var key = (text, line, color, controls);
        if (!previewSourceGeometry.TryGetValue(key, out var row))
        {
            if (previewSourceGeometry.Count >= 256)
            {
                var old = previewSourceGeometry.MinBy(pair => pair.Value.UsedFrame);
                old.Value.Geometry.Dispose(); previewSourceGeometry.Remove(old.Key);
            }
            var geometry = BuildSourceGeometry(text[line], previewFontSize, previewDpi, nativeGlyphRasterizer!, textColor: color, controlColor: controls);
            try { previewSourceGeometry.Add(key, row = new(geometry, (line + 1).ToString(CultureInfo.InvariantCulture))); }
            catch { geometry.Dispose(); throw; }
        }
        row.UsedFrame = Gui.GetFrameCount(); return row;
    }
}
