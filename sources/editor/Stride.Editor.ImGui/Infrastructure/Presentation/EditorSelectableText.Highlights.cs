using System.Text;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed record EditorSelectableTextHighlight(int StartByte, int LengthByte);

internal sealed unsafe partial class EditorSelectableText
{
    private int[] displayLineStarts = [0];
    private (int Start, int End)? requestedSelection;
    private bool focusSelection, revealSelection;
    private static int FindLine(int[] starts, int offset)
    {
        var low = 0; var high = starts.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (starts[middle] <= offset) low = middle + 1;
            else high = middle;
        }
        return Math.Max(0, low - 1);
    }
    internal void SelectSourceBytes(int start, int length, bool focus = true)
    {
        var first = Math.Max(0, start);
        var last = (int)Math.Clamp((long)start + Math.Max(0, length), first, int.MaxValue);
        requestedSelection = (first, last);
        focusSelection |= focus;
    }
    private int FindDisplayOffset(int sourceByte)
    {
        sourceByte = Math.Clamp(sourceByte, 0, original.Length);
        var low = 0; var high = sourceOffsets.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (sourceOffsets[middle] < sourceByte) low = middle + 1;
            else high = middle;
        }
        return Math.Clamp(low, 0, buffer.Length - 1);
    }
    private void DrawHighlights(IReadOnlyList<EditorSelectableTextHighlight> highlights, bool paintText = true)
    {
        var draw = textWindow.DrawList;
        var clip = textWindow.InnerClipRect;
        var origin = textWindow.Pos + textWindow.WindowPadding - textWindow.Scroll;
        var height = Gui.GetTextLineHeight();
        var background = Gui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(176 / 255f, 196 / 255f, 222 / 255f, 1));
        draw.PushClipRect(clip.Min, clip.Max, true);
        try
        {
            var firstLine = Math.Clamp((int)Math.Floor((clip.Min.Y - origin.Y) / height), 0, displayLineStarts.Length - 1);
            var lastLine = Math.Clamp((int)Math.Ceiling((clip.Max.Y - origin.Y) / height), firstLine, displayLineStarts.Length - 1);
            var low = 0; var high = highlights.Count;
            var firstByte = sourceOffsets[displayLineStarts[firstLine]];
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                var item = highlights[middle];
                if (item.StartByte + item.LengthByte <= firstByte) low = middle + 1;
                else high = middle;
            }
            var range = low;
            for (var line = firstLine; line <= lastLine; line++)
            {
                var start = displayLineStarts[line];
                var end = line + 1 < displayLineStarts.Length ? displayLineStarts[line + 1] - 1 : buffer.Length - 1;
                while (range < highlights.Count && highlights[range].StartByte + highlights[range].LengthByte <= sourceOffsets[start]) range++;
                if (origin.Y + (line + 1) * height >= clip.Min.Y && origin.Y + line * height <= clip.Max.Y)
                {
                    for (var index = range; index < highlights.Count && highlights[index].StartByte < sourceOffsets[end]; index++)
                    {
                        var item = highlights[index];
                        var first = Math.Max(start, FindDisplayOffset(item.StartByte));
                        var last = Math.Min(end, FindDisplayOffset(item.StartByte + item.LengthByte));
                        if (last <= first) continue;
                        // Native selection already paints its own background.
                        if (Active && Math.Min(selectionStart, selectionEnd) < last && Math.Max(selectionStart, selectionEnd) > first) continue;
                        var before = Encoding.UTF8.GetString(buffer.AsSpan(start, first - start));
                        var label = Encoding.UTF8.GetString(buffer.AsSpan(first, last - first));
                        var position = origin + new Vector2(Gui.CalcTextSize(before).X, line * height);
                        draw.AddRectFilled(position, position + new Vector2(Gui.CalcTextSize(label).X, height), background);
                        if (paintText) draw.AddText(position, Gui.GetColorU32(ImGuiCol.Text), label);
                    }
                }
            }
        }
        finally { draw.PopClipRect(); }
    }
}
