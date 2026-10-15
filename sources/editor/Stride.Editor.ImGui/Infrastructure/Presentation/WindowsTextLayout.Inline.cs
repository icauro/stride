using System.Numerics;
using System.Runtime.InteropServices;

namespace Stride.Editor.ImGui;

internal sealed unsafe partial class WindowsTextLayout
{
    internal readonly record struct InlineBoxDefinition(int Start, int Length, string Label, float Width, float Height, float Baseline);
    private void InstallInlineBoxes(IReadOnlyList<InlineBoxDefinition>? boxes)
    {
        if (boxes == null) return;
        var previousEnd = 0;
        foreach (var box in boxes)
        {
            if (box.Start < previousEnd || box.Length <= 0 || box.Start > Text.Length - box.Length
                || !float.IsFinite(box.Width) || box.Width <= 0 || !float.IsFinite(box.Height) || box.Height <= 0
                || !float.IsFinite(box.Baseline) || box.Baseline < 0 || box.Baseline > box.Height)
                throw new ArgumentException("Native inline source boxes must have ordered original spans and finite metrics.", nameof(boxes));
            var adapter = new InlineBox(box);
            var pointer = Marshal.GetComInterfaceForObject(adapter, typeof(IWindowsNativeInlineObject));
            try
            {
                var range = new NativeTextRange { Start = (uint)box.Start, Length = (uint)box.Length };
                Check(((delegate* unmanaged[Stdcall]<nint, nint, NativeTextRange, int>)Table(native)[39])(native, pointer, range));
            }
            finally { Marshal.Release(pointer); GC.KeepAlive(adapter); }
            previousEnd = checked(box.Start + box.Length);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeTextRange { internal uint Start, Length; }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class InlineBox(InlineBoxDefinition definition) : IWindowsNativeInlineObject
    {
        internal readonly InlineBoxDefinition Definition = definition;
        public int Draw(nint context, nint renderer, float x, float y, int sideways, int rightToLeft, nint effect)
            => unchecked((int)0x80004001); // This layout's collector owns actual inline placement/drawing.
        public int GetMetrics(out WindowsNativeInlineMetrics metrics)
        {
            metrics = new() { Width = Definition.Width, Height = Definition.Height, Baseline = Definition.Baseline }; return 0;
        }
        public int GetOverhangMetrics(out WindowsNativeOverhangMetrics metrics) { metrics = default; return 0; }
        public int GetBreakConditions(out uint before, out uint after) { before = after = 0; return 0; }
    }
    internal sealed class InlinePlacement(InlineBoxDefinition definition, Vector2 origin, bool sideways, bool rightToLeft, nint effect) : IDisposable
    {
        internal readonly InlineBoxDefinition Definition = definition;
        internal readonly Vector2 Origin = origin;
        internal readonly bool Sideways = sideways, RightToLeft = rightToLeft;
        internal nint DrawingEffect { get; private set; } = Retained(effect);
        private static nint Retained(nint value) { Retain(value); return value; }
        public void Dispose() { Release(DrawingEffect); DrawingEffect = 0; }
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct WindowsNativeInlineMetrics { public float Width, Height, Baseline; public int SupportsSideways; }
[StructLayout(LayoutKind.Sequential)]
public struct WindowsNativeOverhangMetrics { public float Left, Top, Right, Bottom; }
[ComVisible(true), Guid("8339fde3-106f-47ab-8373-1c6295eb10b3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWindowsNativeInlineObject
{
    [PreserveSig] int Draw(nint context, nint renderer, float x, float y, int sideways, int rightToLeft, nint effect);
    [PreserveSig] int GetMetrics(out WindowsNativeInlineMetrics metrics);
    [PreserveSig] int GetOverhangMetrics(out WindowsNativeOverhangMetrics metrics);
    [PreserveSig] int GetBreakConditions(out uint before, out uint after);
}
