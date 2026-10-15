using System.Text;

namespace Stride.Editor.ImGui;

internal static class NativeMiddlePathTrimming
{
    // Original Trimming: WordEllipsis/Middle with both native path separators.
    internal static string Fit(string text, float width, Func<string, float> measure)
    {
        var words = new List<string>(); var word = new StringBuilder();
        foreach (var character in text)
        {
            if (character is '/' or '\\')
            {
                if (word.Length > 0) words.Add(word.ToString());
                word.Clear(); words.Add(character.ToString());
            }
            else word.Append(character);
        }
        if (word.Length > 0) words.Add(word.ToString());
        var period = measure(".");
        float Size(string token) => measure(token + ".") - period;
        var sizes = words.Select(Size).ToArray();
        if (sizes.Sum() <= width || words.Count == 0) return text;
        var used = Size("…"); var start = new StringBuilder(); var end = new StringBuilder();
        for (int first = 0, last = words.Count - 1; first <= last; first++, last--)
        {
            var next = used + sizes[first] + (first == last ? 0 : sizes[last]);
            if (next > width)
            {
                if (first == 0)
                {
                    // Exact native first-word fallback: character trimming from end
                    // of the final word, rather than inventing path abbreviation.
                    var output = new StringBuilder(); var total = Size("…");
                    foreach (var character in words[last])
                    {
                        var size = Size(character.ToString());
                        if (total + size > width) break;
                        output.Append(character); total += size;
                    }
                    return output + "…";
                }
                break;
            }
            start.Append(words[first]); if (first != last) end.Insert(0, words[last]); used = next;
        }
        return start.ToString() + "…" + end;
    }
}
