using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

// Optional exports are resolved from the already loaded binding module, never from a second library.
internal sealed unsafe class WindowsImGuiTabTextHooks : IDisposable
{
    private readonly nint context;
    private readonly Func<string, float, Vector2?> measure;
    private readonly Func<string, ImDrawListPtr, Vector2, Vector2, float, uint, bool> paint;
    private readonly delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void> register;
    private GCHandle owner;
    internal Exception? CallbackFailure { get; private set; }
    internal Exception? TakeFailure() { var failure = CallbackFailure; CallbackFailure = null; return failure; }
    private WindowsImGuiTabTextHooks(nint context, nint register,
        Func<string, float, Vector2?> measure,
        Func<string, ImDrawListPtr, Vector2, Vector2, float, uint, bool> paint)
    {
        this.context = context; this.measure = measure; this.paint = paint;
        this.register = (delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)register;
        owner = GCHandle.Alloc(this);
        this.register(context, GCHandle.ToIntPtr(owner),
            (nint)(delegate* unmanaged[Cdecl]<nint, byte*, byte*, float, Vector2*, byte>)&Measure,
            (nint)(delegate* unmanaged[Cdecl]<nint, ImDrawList*, byte*, byte*, Vector2, Vector2, float, uint, byte>)&Paint);
    }
    internal static WindowsImGuiTabTextHooks? TryCreate(
        Func<string, float, Vector2?> measure,
        Func<string, ImDrawListPtr, Vector2, Vector2, float, uint, bool> paint)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var module = GetModuleHandleW("cimgui.dll");
        if (module == 0 || !NativeLibrary.TryGetExport(module, "stride_imgui_text_hook_version", out var version)
            || !NativeLibrary.TryGetExport(module, "stride_imgui_set_tab_text_hooks", out var register)
            || !NativeLibrary.TryGetExport(module, "igGetCurrentContext", out var current)
            || !NativeLibrary.TryGetExport(module, "igGetVersion", out var nativeVersion)) return null;
        if (((delegate* unmanaged[Cdecl]<uint>)version)() != 1) return null;
        var context = (nint)Gui.GetCurrentContext().Handle;
        // Matching the exact active context proves this is the module used by the managed binding.
        if (context == 0 || ((delegate* unmanaged[Cdecl]<nint>)current)() != context
            || Marshal.PtrToStringUTF8(((delegate* unmanaged[Cdecl]<nint>)nativeVersion)()) != "1.92.2b") return null;
        return new(context, register, measure, paint);
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte Measure(nint user, byte* begin, byte* end, float size, Vector2* result)
    {
        var view = (WindowsImGuiTabTextHooks?)GCHandle.FromIntPtr(user).Target;
        if (view == null || view.CallbackFailure != null) return 0;
        try
        {
            var text = Decode(begin, end);
            if (text == null || view.measure(text, size) is not { } measured) return 0;
            *result = measured; return 1;
        }
        catch (Exception error) { view.CallbackFailure ??= error; return 0; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte Paint(nint user, ImDrawList* draw, byte* begin, byte* end, Vector2 minimum, Vector2 maximum, float ellipsisMax, uint color)
    {
        var view = (WindowsImGuiTabTextHooks?)GCHandle.FromIntPtr(user).Target;
        if (view == null || view.CallbackFailure != null) return 0;
        try
        {
            var text = Decode(begin, end);
            return text != null && view.paint(text, new(draw), minimum, maximum, ellipsisMax, color) ? (byte)1 : (byte)0;
        }
        catch (Exception error) { view.CallbackFailure ??= error; return 0; }
    }
    private static string? Decode(byte* begin, byte* end)
    {
        var length = end - begin;
        return begin == null || length < 0 || length > int.MaxValue ? null : Encoding.UTF8.GetString(new ReadOnlySpan<byte>(begin, (int)length));
    }
    public void Dispose()
    {
        if (!owner.IsAllocated) return;
        register(context, 0, 0, 0);
        owner.Free();
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint GetModuleHandleW(string module);
}
