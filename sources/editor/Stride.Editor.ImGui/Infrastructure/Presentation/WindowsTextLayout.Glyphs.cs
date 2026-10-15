using System.Numerics;
using System.Runtime.InteropServices;

namespace Stride.Editor.ImGui;

internal sealed unsafe partial class WindowsTextLayout
{
    internal sealed class GlyphRun : IDisposable
    {
        internal nint FontFace { get; private set; }
        internal nint DrawingEffect { get; private set; }
        internal readonly float Size;
        internal readonly Vector2 Baseline;
        internal readonly uint MeasuringMode, BidiLevel;
        internal readonly bool Sideways;
        internal readonly int TextPosition, TextLength;
        internal readonly ushort[] Indices, ClusterMap;
        internal readonly float[]? Advances;
        internal readonly NativeGlyphOffset[]? Offsets;
        internal GlyphRun(NativeGlyphRun run, NativeGlyphDescription description, Vector2 baseline, uint mode, nint effect)
        {
            Size = run.Size; Baseline = baseline; MeasuringMode = mode; BidiLevel = run.BidiLevel; Sideways = run.Sideways != 0;
            TextPosition = checked((int)description.Position); TextLength = checked((int)description.Length);
            var count = checked((int)run.Count);
            if (run.Face == 0 || count != 0 && run.Indices == null) throw new InvalidDataException("The native shaped glyph run is incomplete.");
            Indices = new ReadOnlySpan<ushort>(run.Indices, count).ToArray();
            // DirectWrite permits absent advances/offsets; retain that native contract.
            Advances = run.Advances == null ? null : new ReadOnlySpan<float>(run.Advances, count).ToArray();
            Offsets = run.Offsets == null ? null : new ReadOnlySpan<NativeGlyphOffset>(run.Offsets, count).ToArray();
            ClusterMap = description.Clusters == null ? [] : new ReadOnlySpan<ushort>(description.Clusters, TextLength).ToArray();
            Retain(run.Face); FontFace = run.Face; Retain(effect); DrawingEffect = effect;
        }
        public void Dispose() { Release(FontFace); FontFace = 0; Release(DrawingEffect); DrawingEffect = 0; }
    }
    internal sealed class GlyphSnapshot : IDisposable
    {
        internal readonly IReadOnlyList<GlyphRun> Runs;
        internal readonly IReadOnlyList<Decoration> Decorations;
        internal readonly IReadOnlyList<InlinePlacement> Inlines;
        internal GlyphSnapshot(GlyphCollector collector) { Runs = collector.Runs.ToArray(); Decorations = collector.Decorations.ToArray(); Inlines = collector.Inlines.ToArray(); }
        public void Dispose() { foreach (var run in Runs) run.Dispose(); foreach (var decoration in Decorations) decoration.Dispose(); foreach (var inline in Inlines) inline.Dispose(); }
    }
    internal sealed class Decoration(Vector2 baseline, float width, float thickness, float offset, uint readingDirection, uint flowDirection, uint measuringMode, nint effect) : IDisposable
    {
        internal readonly Vector2 Baseline = baseline;
        internal readonly float Width = width, Thickness = thickness, Offset = offset;
        internal readonly uint ReadingDirection = readingDirection, FlowDirection = flowDirection, MeasuringMode = measuringMode;
        internal nint DrawingEffect { get; private set; } = RetainedEffect(effect);
        private static nint RetainedEffect(nint value) { Retain(value); return value; }
        public void Dispose() { Release(DrawingEffect); DrawingEffect = 0; }
    }
    internal GlyphSnapshot Shape()
    {
        CheckOwner(); var collector = new GlyphCollector(Text.Length);
        var callback = Marshal.GetComInterfaceForObject(collector, typeof(IWindowsNativeTextRenderer));
        try
        {
            var result = ((delegate* unmanaged[Stdcall]<nint, nint, nint, float, float, int>)Table(native)[58])(native, 0, callback, 0, 0);
            if (collector.Error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(collector.Error).Throw();
            Check(result); return new(collector);
        }
        catch { foreach (var run in collector.Runs) run.Dispose(); foreach (var decoration in collector.Decorations) decoration.Dispose(); foreach (var inline in collector.Inlines) inline.Dispose(); throw; }
        finally { Marshal.Release(callback); GC.KeepAlive(collector); }
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class GlyphCollector(int length) : IWindowsNativeTextRenderer, IWindowsNativePixelSnapping
    {
        internal readonly List<GlyphRun> Runs = [];
        internal readonly List<Decoration> Decorations = [];
        internal readonly List<InlinePlacement> Inlines = [];
        internal Exception? Error;
        private static void AddOwned<T>(List<T> values, T value) where T : IDisposable
        {
            try { values.Add(value); }
            catch { value.Dispose(); throw; }
        }
        public int IsPixelSnappingDisabled(nint context, out int disabled) { disabled = 1; return 0; }
        public int GetCurrentTransform(nint context, out WindowsNativeTextMatrix transform) { transform = new() { M11 = 1, M22 = 1 }; return 0; }
        public int GetPixelsPerDip(nint context, out float pixels) { pixels = 1; return 0; }
        public int DrawGlyphRun(nint context, float x, float y, uint mode, nint run, nint description, nint effect)
        {
            try
            {
                if (run == 0 || description == 0) throw new InvalidDataException("The native shaped glyph description is missing.");
                var info = *(NativeGlyphDescription*)description;
                if (info.Position > length || info.Length > length - info.Position) throw new InvalidDataException("The native shaped source span is invalid.");
                AddOwned(Runs, new GlyphRun(*(NativeGlyphRun*)run, info, new(x, y), mode, effect)); return 0;
            }
            catch (Exception error) { Error ??= error; return Marshal.GetHRForException(error); }
        }
        public int DrawUnderline(nint context, float x, float y, nint underline, nint effect)
        {
            try
            {
                if (underline == 0) throw new ArgumentException("The native underline is missing.");
                var value = *(NativeUnderline*)underline;
                AddOwned(Decorations, new Decoration(new(x, y), value.Width, value.Thickness, value.Offset, value.ReadingDirection, value.FlowDirection, value.MeasuringMode, effect)); return 0;
            }
            catch (Exception error) { Error ??= error; return Marshal.GetHRForException(error); }
        }
        public int DrawStrikethrough(nint context, float x, float y, nint strike, nint effect)
        {
            try
            {
                if (strike == 0) throw new ArgumentException("The native strikethrough is missing.");
                var value = *(NativeStrike*)strike;
                AddOwned(Decorations, new Decoration(new(x, y), value.Width, value.Thickness, value.Offset, value.ReadingDirection, value.FlowDirection, value.MeasuringMode, effect)); return 0;
            }
            catch (Exception error) { Error ??= error; return Marshal.GetHRForException(error); }
        }
        public int DrawInlineObject(nint context, float x, float y, nint inline, int sideways, int rightToLeft, nint effect)
        {
            try
            {
                if (inline == 0 || Marshal.GetObjectForIUnknown(inline) is not InlineBox box)
                    throw new NotSupportedException("The native inline object is not owned by this text layout adapter.");
                AddOwned(Inlines, new InlinePlacement(box.Definition, new(x, y), sideways != 0, rightToLeft != 0, effect)); return 0;
            }
            catch (Exception error) { Error ??= error; return Marshal.GetHRForException(error); }
        }
    }
    [StructLayout(LayoutKind.Sequential)] internal struct NativeGlyphOffset { internal float AdvanceOffset, AscenderOffset; }
    [StructLayout(LayoutKind.Sequential)] internal struct NativeGlyphRun
    { internal nint Face; internal float Size; internal uint Count; internal ushort* Indices; internal float* Advances; internal NativeGlyphOffset* Offsets; internal int Sideways; internal uint BidiLevel; }
    [StructLayout(LayoutKind.Sequential)] internal struct NativeGlyphDescription
    { internal nint Locale, Characters; internal uint Length; internal ushort* Clusters; internal uint Position; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeUnderline
    { internal float Width, Thickness, Offset, RunHeight; internal uint ReadingDirection, FlowDirection; internal nint Locale; internal uint MeasuringMode; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeStrike
    { internal float Width, Thickness, Offset; internal uint ReadingDirection, FlowDirection; internal nint Locale; internal uint MeasuringMode; }
}

[StructLayout(LayoutKind.Sequential)]
public struct WindowsNativeTextMatrix { public float M11, M12, M21, M22, Dx, Dy; }
[ComVisible(true), Guid("eaf3a2da-ecf4-4d24-b644-b34f6842024b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWindowsNativePixelSnapping
{
    [PreserveSig] int IsPixelSnappingDisabled(nint context, out int disabled);
    [PreserveSig] int GetCurrentTransform(nint context, out WindowsNativeTextMatrix transform);
    [PreserveSig] int GetPixelsPerDip(nint context, out float pixels);
}
[ComVisible(true), Guid("ef8a8135-5cc6-45fe-8825-c5a0724eb819"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWindowsNativeTextRenderer
{
    [PreserveSig] int IsPixelSnappingDisabled(nint context, out int disabled);
    [PreserveSig] int GetCurrentTransform(nint context, out WindowsNativeTextMatrix transform);
    [PreserveSig] int GetPixelsPerDip(nint context, out float pixels);
    [PreserveSig] int DrawGlyphRun(nint context, float x, float y, uint mode, nint run, nint description, nint effect);
    [PreserveSig] int DrawUnderline(nint context, float x, float y, nint underline, nint effect);
    [PreserveSig] int DrawStrikethrough(nint context, float x, float y, nint strike, nint effect);
    [PreserveSig] int DrawInlineObject(nint context, float x, float y, nint inline, int sideways, int rightToLeft, nint effect);
}
