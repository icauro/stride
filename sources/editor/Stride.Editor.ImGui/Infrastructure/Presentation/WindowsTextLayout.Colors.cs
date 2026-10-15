using System.Runtime.InteropServices;

namespace Stride.Editor.ImGui;

internal sealed unsafe partial class WindowsTextLayout
{
    internal readonly record struct ColorRange(int Start, int Length, uint Color);
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class ColorEffect(uint color) { internal readonly uint Color = color; }
    internal void SetColors(IReadOnlyList<ColorRange> colors)
    {
        CheckOwner(); var previousEnd = 0;
        foreach (var color in colors)
        {
            if (color.Start < previousEnd || color.Length <= 0 || color.Start > Text.Length - color.Length)
                throw new ArgumentException("Native text colors must identify ordered original source spans.", nameof(colors));
            var effect = new ColorEffect(color.Color); var pointer = Marshal.GetIUnknownForObject(effect);
            try
            {
                var range = new NativeTextRange { Start = (uint)color.Start, Length = (uint)color.Length };
                Check(((delegate* unmanaged[Stdcall]<nint, nint, NativeTextRange, int>)Table(native)[38])(native, pointer, range));
            }
            finally { Marshal.Release(pointer); GC.KeepAlive(effect); }
            previousEnd = color.Start;
        }
    }
    internal static uint DrawingColor(nint effect, uint defaultColor)
    {
        if (effect == 0) return defaultColor;
        if (Marshal.GetObjectForIUnknown(effect) is ColorEffect color) return color.Color;
        throw new NotSupportedException("The native text drawing effect has no configured color adapter.");
    }
}
