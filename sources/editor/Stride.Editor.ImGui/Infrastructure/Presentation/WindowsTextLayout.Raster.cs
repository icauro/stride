using System.Runtime.InteropServices;

namespace Stride.Editor.ImGui;

internal sealed unsafe partial class WindowsTextLayout
{
    /// <summary>Rasterizes the retained native glyph IDs and font faces, without re-shaping text.</summary>
    internal sealed class GlyphRasterizer : IDisposable
    {
        private nint factory;
        private readonly int ownerThread = Environment.CurrentManagedThreadId;
        internal GlyphRasterizer()
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            var id = new Guid("0439fc60-ca44-4994-8dee-3a9af7b732ec");
            Check(DWriteCreateFactory(0, in id, out var created)); factory = created;
        }
        internal readonly record struct Bitmap(int Left, int Top, int Width, int Height, byte[] Coverage);
        internal Analysis Analyze(GlyphRun glyphs, float pixelsPerDip)
        {
            CheckRasterOwner();
            if (!float.IsFinite(pixelsPerDip) || pixelsPerDip <= 0) throw new ArgumentOutOfRangeException(nameof(pixelsPerDip));
            if (glyphs.FontFace == 0) throw new ObjectDisposedException(nameof(GlyphRun));
            if (glyphs.Indices.Length == 0) return new(0, default);
            nint analysis = 0;
            try
            {
                var transform = new WindowsNativeTextMatrix { M11 = pixelsPerDip, M22 = pixelsPerDip };
                fixed (ushort* indices = glyphs.Indices)
                fixed (float* advances = glyphs.Advances)
                fixed (NativeGlyphOffset* offsets = glyphs.Offsets)
                {
                    var run = new NativeGlyphRun
                    {
                        Face = glyphs.FontFace, Size = glyphs.Size, Count = checked((uint)glyphs.Indices.Length),
                        Indices = indices, Advances = advances, Offsets = offsets,
                        Sideways = glyphs.Sideways ? 1 : 0, BidiLevel = glyphs.BidiLevel,
                    };
                    // Factory2: NATURAL_SYMMETRIC, enabled grid fitting, grayscale alpha.
                    Check(((delegate* unmanaged[Stdcall]<nint, NativeGlyphRun*, WindowsNativeTextMatrix*, uint, uint, uint, uint, float, float, nint*, int>)Table(factory)[30])
                        (factory, &run, &transform, 5, glyphs.MeasuringMode, 2, 1, glyphs.Baseline.X, glyphs.Baseline.Y, &analysis));
                }
                NativePixelRectangle bounds;
                // Grayscale uses the one-byte texture, not the three ClearType subpixel channels.
                Check(((delegate* unmanaged[Stdcall]<nint, uint, NativePixelRectangle*, int>)Table(analysis)[3])(analysis, 0, &bounds));
                var created = new Analysis(analysis, bounds); analysis = 0; return created;
            }
            finally { Release(analysis); GC.KeepAlive(glyphs); }
        }
        internal sealed class Analysis : IDisposable
        {
            private nint native;
            private readonly int owner = Environment.CurrentManagedThreadId;
            internal readonly NativePixelRectangle Bounds;
            private bool disposed;
            internal Analysis(nint value, NativePixelRectangle bounds)
            {
                if (bounds.Right < bounds.Left || bounds.Bottom < bounds.Top) throw new InvalidDataException("The native glyph bounds are invalid.");
                native = value; Bounds = bounds;
            }
            internal Bitmap Rasterize(int left, int top, int right, int bottom)
            {
                CheckAnalysisOwner();
                var area = new NativePixelRectangle { Left = Math.Max(left, Bounds.Left), Top = Math.Max(top, Bounds.Top), Right = Math.Min(right, Bounds.Right), Bottom = Math.Min(bottom, Bounds.Bottom) };
                var width = Math.Max(0, checked(area.Right - area.Left)); var height = Math.Max(0, checked(area.Bottom - area.Top));
                var coverage = new byte[checked(width * height)];
                if (coverage.Length != 0)
                    fixed (byte* bytes = coverage)
                        Check(((delegate* unmanaged[Stdcall]<nint, uint, NativePixelRectangle*, byte*, uint, int>)Table(native)[4])
                            (native, 0, &area, bytes, checked((uint)coverage.Length)));
                return new(area.Left, area.Top, width, height, coverage);
            }
            private void CheckAnalysisOwner()
            {
                if (Environment.CurrentManagedThreadId != owner) throw new InvalidOperationException("Native glyph analysis belongs to its actual owner thread.");
                ObjectDisposedException.ThrowIf(disposed, this);
            }
            public void Dispose() { if (disposed) return; CheckAnalysisOwner(); Release(native); native = 0; disposed = true; }
        }
        private void CheckRasterOwner()
        {
            if (Environment.CurrentManagedThreadId != ownerThread) throw new InvalidOperationException("Native glyph rasterization belongs to its actual owner thread.");
            if (factory == 0) throw new ObjectDisposedException(nameof(GlyphRasterizer));
        }
        public void Dispose() { if (factory == 0) return; CheckRasterOwner(); Release(factory); factory = 0; }
    }
    [StructLayout(LayoutKind.Sequential)] internal struct NativePixelRectangle { internal int Left, Top, Right, Bottom; }
}
