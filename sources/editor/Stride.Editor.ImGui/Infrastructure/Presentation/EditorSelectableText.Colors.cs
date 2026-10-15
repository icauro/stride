using System.Text;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed unsafe partial class EditorSelectableText
{
    private string? fallbackColoredSource;
    private WindowsTextLayout.ColorRange[] fallbackColorSource = [];
    private WindowsTextLayout.ColorRange[] fallbackByteColors = [];

    private void DrawColoredText()
    {
        var same = fallbackColoredSource == source && fallbackColorSource.Length == NativeTextColors.Count;
        for (var index = 0; same && index < fallbackColorSource.Length; index++)
            same = fallbackColorSource[index] == NativeTextColors[index];
        if (!same)
        {
            var ranges = NativeTextColors.ToArray();
            var bytes = new WindowsTextLayout.ColorRange[ranges.Length];
            var sourceCursor = 0; var byteCursor = 0;
            for (var index = 0; index < ranges.Length; index++)
            {
                var range = ranges[index];
                if (range.Start < sourceCursor || range.Length < 0 || range.Start > source.Length - range.Length)
                    throw new ArgumentOutOfRangeException(nameof(NativeTextColors));
                byteCursor += Encoding.UTF8.GetByteCount(source.AsSpan(sourceCursor, range.Start - sourceCursor));
                var length = Encoding.UTF8.GetByteCount(source.AsSpan(range.Start, range.Length));
                bytes[index] = new(byteCursor, length, range.Color);
                sourceCursor = range.Start + range.Length; byteCursor += length;
            }
            fallbackColoredSource = source; fallbackColorSource = ranges; fallbackByteColors = bytes;
        }
        var draw = textWindow.DrawList;
        var clip = textWindow.InnerClipRect;
        var origin = textWindow.Pos + textWindow.WindowPadding - textWindow.Scroll;
        var height = Gui.GetTextLineHeight();
        var firstLine = Math.Clamp((int)Math.Floor((clip.Min.Y - origin.Y) / height), 0, displayLineStarts.Length - 1);
        var lastLine = Math.Clamp((int)Math.Ceiling((clip.Max.Y - origin.Y) / height), firstLine, displayLineStarts.Length - 1);
        var firstByte = sourceOffsets[displayLineStarts[firstLine]];
        var low = 0; var high = fallbackByteColors.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            var range = fallbackByteColors[middle];
            if (range.Start + range.Length <= firstByte) low = middle + 1;
            else high = middle;
        }
        var rangeIndex = low;
        draw.PushClipRect(clip.Min, clip.Max, true);
        try
        {
            for (var line = firstLine; line <= lastLine; line++)
            {
                var start = displayLineStarts[line];
                var end = line + 1 < displayLineStarts.Length ? displayLineStarts[line + 1] - 1 : buffer.Length - 1;
                if (origin.Y + (line + 1) * height >= clip.Min.Y && origin.Y + line * height <= clip.Max.Y)
                {
                    var segment = start;
                    while (segment < end)
                    {
                        while (rangeIndex < fallbackByteColors.Length
                            && fallbackByteColors[rangeIndex].Start + fallbackByteColors[rangeIndex].Length <= sourceOffsets[segment]) rangeIndex++;
                        var color = Gui.GetColorU32(ImGuiCol.Text);
                        var boundary = int.MaxValue;
                        if (rangeIndex < fallbackByteColors.Length)
                        {
                            var range = fallbackByteColors[rangeIndex];
                            if (sourceOffsets[segment] >= range.Start) { color = range.Color; boundary = range.Start + range.Length; }
                            else boundary = range.Start;
                        }
                        var next = segment + 1;
                        while (next < end && sourceOffsets[next] < boundary) next++;
                        var prefix = Encoding.UTF8.GetString(buffer.AsSpan(start, segment - start));
                        var label = Encoding.UTF8.GetString(buffer.AsSpan(segment, next - segment));
                        draw.AddText(origin + new Vector2(Gui.CalcTextSize(prefix).X, line * height), color, label);
                        segment = next;
                    }
                }
            }
        }
        finally { draw.PopClipRect(); }
    }
}
