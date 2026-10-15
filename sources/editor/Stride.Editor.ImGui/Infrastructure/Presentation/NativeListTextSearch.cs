using System.Globalization;

namespace Stride.Editor.ImGui;

/// <summary>Original WPF TextSearch prefix/repeated-character algorithm without a dispatcher timer.</summary>
internal sealed class NativeListTextSearch
{
    private readonly List<string> characters = [];
    internal string Prefix { get; private set; } = "";
    internal int MatchedIndex { get; set; } = -1;
    internal bool ShouldNavigate { get; private set; }
    private bool active;
    private double expires;
    internal void Expire(double now) { if (active && now >= expires) Reset(); }
    internal void Reset() { active = false; Prefix = ""; MatchedIndex = -1; characters.Clear(); }
    internal void Backspace(double now, double timeout)
    {
        Expire(now);
        if (!active || characters.Count == 0) return;
        var last = characters[^1]; characters.RemoveAt(characters.Count - 1);
        Prefix = Prefix[..^last.Length]; expires = now + timeout;
    }
    internal int Search(string next, int count, Func<int, string> text, double now, double timeout, CultureInfo culture)
    {
        Expire(now); ShouldNavigate = false; if (count == 0) return -1;
        var start = active && MatchedIndex >= 0 && MatchedIndex < count ? MatchedIndex : 0;
        var repeated = characters.Count > 0 && string.Compare(characters[^1], next, true, culture) == 0;
        var nextPrefix = Prefix + next; if (nextPrefix.Length == 0) return -1;
        var match = -1; var fallback = -1; var used = false;
        for (var offset = 0; offset < count; offset++)
        {
            var index = (start + offset) % count; var candidate = text(index);
            if (candidate.StartsWith(nextPrefix, true, culture)) { match = index; used = true; break; }
            if (repeated && offset > 0 && fallback < 0 && Prefix.Length != 0 && candidate.StartsWith(Prefix, true, culture)) fallback = index;
        }
        if (match < 0) match = fallback;
        if (match >= 0)
        {
            ShouldNavigate = !active || match != start;
            if (used) { Prefix = nextPrefix; characters.Add(next); }
            MatchedIndex = match; active = true;
        }
        if (active) expires = now + timeout;
        return match;
    }
}
