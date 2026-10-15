using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Stride.Editor.ImGui;

/// <summary>Real DirectWrite layout over immutable original UTF16 text. Geometry is in DIPs.</summary>
internal sealed unsafe partial class WindowsTextLayout : IDisposable
{
    private nint native;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    internal string Text { get; }
    internal NativeTextMetrics Metrics { get; private set; }
    internal WindowsTextLayout(string text, string family, float size, float width, bool wrap = false, bool rightToLeft = false, string? locale = null, IReadOnlyList<InlineBoxDefinition>? boxes = null, bool uniformLineHeight = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native Windows text layout is unavailable.");
        if (!float.IsFinite(size) || size <= 0 || !float.IsFinite(width) || width <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        Text = text;
        nint factory = 0, format = 0;
        try
        {
            var id = new Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");
            Check(DWriteCreateFactory(0, in id, out factory));
            var culture = locale ?? CultureInfo.CurrentUICulture.Name;
            fixed (char* fontName = family)
            fixed (char* language = culture)
                Check(((delegate* unmanaged[Stdcall]<nint, char*, nint, uint, uint, uint, float, char*, nint*, int>)Table(factory)[15])
                    (factory, fontName, 0, 400, 0, 5, size, language, &format));
            Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Table(format)[5])(format, wrap ? 0u : 1u));
            Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Table(format)[6])(format, rightToLeft ? 1u : 0u));
            // Native space metrics determine the same four-column source tab stop.
            nint space = 0;
            try
            {
                const string sample = " ";
                fixed (char* value = sample)
                    Check(((delegate* unmanaged[Stdcall]<nint, char*, uint, nint, float, float, nint*, int>)Table(factory)[18])(factory, value, 1, format, width, 1_000_000, &space));
                NativeTextMetrics metrics;
                Check(((delegate* unmanaged[Stdcall]<nint, NativeTextMetrics*, int>)Table(space)[60])(space, &metrics));
                Check(((delegate* unmanaged[Stdcall]<nint, float, int>)Table(format)[8])(format, Math.Max(.01f, metrics.WidthIncludingTrailingWhitespace) * 4));
                if (uniformLineHeight)
                {
                    NativeLineMetrics line; uint count;
                    Check(((delegate* unmanaged[Stdcall]<nint, NativeLineMetrics*, uint, uint*, int>)Table(space)[59])(space, &line, 1, &count));
                    Check(((delegate* unmanaged[Stdcall]<nint, uint, float, float, int>)Table(format)[10])(format, 1, line.Height, line.Baseline));
                }
            }
            finally { Release(space); }
            nint created = 0;
            fixed (char* value = text)
                Check(((delegate* unmanaged[Stdcall]<nint, char*, uint, nint, float, float, nint*, int>)Table(factory)[18])
                    (factory, value, checked((uint)text.Length), format, width, 1_000_000, &created));
            native = created;
            InstallInlineBoxes(boxes);
            NativeTextMetrics result;
            Check(((delegate* unmanaged[Stdcall]<nint, NativeTextMetrics*, int>)Table(native)[60])(native, &result));
            Metrics = result;
        }
        catch { Release(native); native = 0; throw; }
        finally { Release(format); Release(factory); }
    }
    internal readonly record struct Hit(int Position, bool Trailing, bool Inside, NativeHitMetrics Metrics);
    internal Hit HitTest(Vector2 point)
    {
        CheckOwner(); int trailing, inside; NativeHitMetrics metrics;
        Check(((delegate* unmanaged[Stdcall]<nint, float, float, int*, int*, NativeHitMetrics*, int>)Table(native)[64])(native, point.X, point.Y, &trailing, &inside, &metrics));
        var position = checked((int)(metrics.TextPosition + (trailing != 0 ? metrics.Length : 0)));
        return new(Math.Clamp(position, 0, Text.Length), trailing != 0, inside != 0, metrics);
    }
    internal (Vector2 Point, NativeHitMetrics Metrics) Caret(int position, bool trailing = false)
    {
        CheckOwner(); if (position < 0 || position > Text.Length) throw new ArgumentOutOfRangeException(nameof(position));
        float x, y; NativeHitMetrics metrics;
        Check(((delegate* unmanaged[Stdcall]<nint, uint, int, float*, float*, NativeHitMetrics*, int>)Table(native)[65])
            (native, (uint)position, trailing ? 1 : 0, &x, &y, &metrics));
        return (new(x, y), metrics);
    }
    internal NativeHitMetrics[] Selection(int start, int length)
    {
        CheckOwner(); if (start < 0 || length < 0 || start > Text.Length - length) throw new ArgumentOutOfRangeException(nameof(start));
        if (length == 0) return [];
        uint count = 0;
        var result = ((delegate* unmanaged[Stdcall]<nint, uint, uint, float, float, NativeHitMetrics*, uint, uint*, int>)Table(native)[66])
            (native, (uint)start, (uint)length, 0, 0, null, 0, &count);
        CheckCountQuery(result, count);
        var rectangles = new NativeHitMetrics[checked((int)count)];
        fixed (NativeHitMetrics* values = rectangles)
            Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, float, float, NativeHitMetrics*, uint, uint*, int>)Table(native)[66])
                (native, (uint)start, (uint)length, 0, 0, values, (uint)rectangles.Length, &count));
        return rectangles;
    }
    internal NativeClusterMetrics[] Clusters()
    {
        CheckOwner(); uint count = 0;
        var result = ((delegate* unmanaged[Stdcall]<nint, NativeClusterMetrics*, uint, uint*, int>)Table(native)[62])(native, null, 0, &count);
        CheckCountQuery(result, count);
        var clusters = new NativeClusterMetrics[checked((int)count)];
        fixed (NativeClusterMetrics* values = clusters)
            Check(((delegate* unmanaged[Stdcall]<nint, NativeClusterMetrics*, uint, uint*, int>)Table(native)[62])(native, values, (uint)clusters.Length, &count));
        return clusters;
    }
    internal NativeLineMetrics[] Lines()
    {
        CheckOwner(); uint count = 0;
        var result = ((delegate* unmanaged[Stdcall]<nint, NativeLineMetrics*, uint, uint*, int>)Table(native)[59])(native, null, 0, &count);
        CheckCountQuery(result, count);
        var lines = new NativeLineMetrics[checked((int)count)];
        fixed (NativeLineMetrics* values = lines)
            Check(((delegate* unmanaged[Stdcall]<nint, NativeLineMetrics*, uint, uint*, int>)Table(native)[59])(native, values, (uint)lines.Length, &count));
        return lines;
    }
    private void CheckOwner()
    {
        if (Environment.CurrentManagedThreadId != ownerThread) throw new InvalidOperationException("Native text layout belongs to its actual owner thread.");
        if (native == 0) throw new ObjectDisposedException(nameof(WindowsTextLayout));
    }
    public void Dispose() { if (native == 0) return; CheckOwner(); Release(native); native = 0; }
    private static void CheckCountQuery(int result, uint count) { if (result != unchecked((int)0x8007007A)) Check(result); if (count > int.MaxValue) throw new InvalidDataException("The native layout metrics are too large."); }
    private static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
    private static nint* Table(nint value) => *(nint**)value;
    private static void Release(nint value) { if (value != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Table(value)[2])(value); }
    private static void Retain(nint value) { if (value != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Table(value)[1])(value); }
    [DllImport("dwrite.dll", ExactSpelling = true)] private static extern int DWriteCreateFactory(uint type, in Guid id, out nint factory);
    [StructLayout(LayoutKind.Sequential)] internal struct NativeTextMetrics
    { internal float Left, Top, Width, WidthIncludingTrailingWhitespace, Height, LayoutWidth, LayoutHeight; internal uint MaxBidiReorderingDepth, LineCount; }
    [StructLayout(LayoutKind.Sequential)] internal struct NativeHitMetrics
    { internal uint TextPosition, Length; internal float Left, Top, Width, Height; internal uint BidiLevel; internal int IsText, IsTrimmed; }
    [StructLayout(LayoutKind.Sequential)] internal struct NativeClusterMetrics
    { internal float Width; internal ushort Length, Flags; internal bool RightToLeft => (Flags & 16) != 0; }
    [StructLayout(LayoutKind.Sequential)] internal struct NativeLineMetrics
    { internal uint Length, TrailingWhitespaceLength, NewlineLength; internal float Height, Baseline; internal int IsTrimmed; }
}
