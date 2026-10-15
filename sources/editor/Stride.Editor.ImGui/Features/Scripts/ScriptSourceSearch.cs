using System.Globalization;
using System.Text.RegularExpressions;

namespace Stride.Editor.ImGui;

internal sealed record ScriptSearchSnapshot(string Text, long Version, string Pattern, bool MatchCase, bool WholeWords, bool Regex);
internal sealed record ScriptSearchResult(ScriptSearchSnapshot Snapshot, Match[] Matches, string? Error);

/// <summary>Detached real text search using the reference editor's regex and Unicode word-border policy.</summary>
internal static class ScriptSourceSearch
{
    internal static ScriptSearchResult Find(ScriptSearchSnapshot snapshot, CancellationToken cancellation)
    {
        try
        {
            if (snapshot.Pattern.Length == 0) return new(snapshot, [], null);
            var expression = snapshot.Regex ? snapshot.Pattern : Regex.Escape(snapshot.Pattern);
            var options = RegexOptions.Multiline | (snapshot.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
            var regex = new Regex(expression, options, TimeSpan.FromSeconds(2));
            var matches = new List<Match>();
            foreach (Match match in regex.Matches(snapshot.Text))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!snapshot.WholeWords || Border(snapshot.Text, match.Index) && Border(snapshot.Text, match.Index + match.Length)) matches.Add(match);
            }
            cancellation.ThrowIfCancellationRequested();
            return new(snapshot, matches.ToArray(), null);
        }
        catch (Exception error) when (error is ArgumentException or RegexMatchTimeoutException) { return new(snapshot, [], error.Message); }
    }
    // AvalonEdit WordBorder distinguishes identifier, punctuation, whitespace,
    // line terminators and combining marks; it never splits a surrogate pair.
    private static bool Border(string text, int offset)
    {
        if (text.Length == 0) return false;
        if (offset == 0) return !char.IsWhiteSpace(text[0]);
        if (offset == text.Length) return !char.IsWhiteSpace(text[^1]);
        if (char.IsSurrogatePair(text[offset - 1], text[offset])) return false;
        var after = CharacterClass(text, offset);
        if (after == 3) return false;
        var before = offset - 1;
        if (char.IsLowSurrogate(text[before]) && before > 0 && char.IsHighSurrogate(text[before - 1])) before--;
        return CharacterClass(text, before) != after;
    }
    private static int CharacterClass(string text, int index)
    {
        if (text[index] is '\r' or '\n') return 4;
        if (text[index] == '_') return 2;
        return char.GetUnicodeCategory(text, index) switch
        {
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber => 2,
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark => 3,
            UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Control => 1,
            _ => 0,
        };
    }
}
