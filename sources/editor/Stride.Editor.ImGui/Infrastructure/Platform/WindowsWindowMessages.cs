using System.Runtime.InteropServices;

namespace Stride.Editor.ImGui;

/// <summary>Observes the owned main HWND without replacing Stride/SDL's window procedure.</summary>
internal sealed class WindowsWindowMessages : IDisposable
{
    private const uint Id = 0x494D494E;
    private nint window;
    private readonly SubclassProcedure procedure;
    private readonly Action<nint, uint, nuint, nint> observer;
    private readonly Action<string> report;
    public WindowsWindowMessages(nint window, Action<nint, uint, nuint, nint> observer, Action<string> report)
    {
        this.window = window; this.observer = observer; this.report = report; procedure = OnMessage;
        if (!SetWindowSubclass(window, procedure, Id, 0))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    private nint OnMessage(nint hwnd, uint message, nuint wparam, nint lparam, nuint id, nuint data)
    {
        try { observer(hwnd, message, wparam, lparam); }
        catch (Exception error) { report("Native input: " + error); }
        return DefSubclassProc(hwnd, message, wparam, lparam);
    }
    public void Dispose()
    {
        if (window == 0) return;
        if (IsWindow(window) && !RemoveWindowSubclass(window, procedure, Id))
            throw new InvalidOperationException("Could not release the editor's native input callback.");
        window = 0; GC.KeepAlive(procedure);
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProcedure(nint hwnd, uint message, nuint wparam, nint lparam, nuint id, nuint data);
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassProcedure callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProcedure callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
}
