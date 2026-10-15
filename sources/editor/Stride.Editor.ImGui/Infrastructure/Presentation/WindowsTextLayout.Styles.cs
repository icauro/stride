namespace Stride.Editor.ImGui;

internal sealed unsafe partial class WindowsTextLayout
{
    internal readonly record struct StyleRange(int Start, int Length, float Size, uint Weight = 400, bool Underline = false, bool Italic = false);
    internal void SetStyles(IReadOnlyList<StyleRange> styles)
    {
        CheckOwner();
        foreach (var style in styles)
        {
            if (style.Start < 0 || style.Length <= 0 || style.Start > Text.Length - style.Length || !float.IsFinite(style.Size) || style.Size <= 0 || style.Weight is < 1 or > 999)
                throw new ArgumentException("Native text styles must identify actual source spans and valid font metrics.", nameof(styles));
            var range = new NativeTextRange { Start = (uint)style.Start, Length = (uint)style.Length };
            Check(((delegate* unmanaged[Stdcall]<nint, uint, NativeTextRange, int>)Table(native)[32])(native, style.Weight, range));
            Check(((delegate* unmanaged[Stdcall]<nint, uint, NativeTextRange, int>)Table(native)[33])(native, style.Italic ? 2u : 0u, range));
            Check(((delegate* unmanaged[Stdcall]<nint, float, NativeTextRange, int>)Table(native)[35])(native, style.Size, range));
            Check(((delegate* unmanaged[Stdcall]<nint, int, NativeTextRange, int>)Table(native)[36])(native, style.Underline ? 1 : 0, range));
        }
        NativeTextMetrics metrics;
        Check(((delegate* unmanaged[Stdcall]<nint, NativeTextMetrics*, int>)Table(native)[60])(native, &metrics));
        Metrics = metrics;
    }
    internal void SetUnderlines(IReadOnlyList<(int Start, int Length)> ranges)
    {
        CheckOwner();
        foreach (var item in ranges)
        {
            if (item.Start < 0 || item.Length <= 0 || item.Start > Text.Length - item.Length) throw new ArgumentException("Native text underlines must identify actual source spans.", nameof(ranges));
            var range = new NativeTextRange { Start = (uint)item.Start, Length = (uint)item.Length };
            Check(((delegate* unmanaged[Stdcall]<nint, int, NativeTextRange, int>)Table(native)[36])(native, 1, range));
        }
    }
}
