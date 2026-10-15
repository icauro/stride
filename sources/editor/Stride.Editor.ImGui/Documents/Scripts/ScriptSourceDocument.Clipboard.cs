namespace Stride.Editor.ImGui;

internal partial class ScriptSourceDocument
{
    internal string CopySourceRange(int start, int length)
    {
        if (start < 0 || length < 0 || start > EditorText.Length - length) throw new ArgumentOutOfRangeException(nameof(start));
        var first = RawOffset(start); var last = RawOffset(start + length);
        return Text[first..last];
    }
}
