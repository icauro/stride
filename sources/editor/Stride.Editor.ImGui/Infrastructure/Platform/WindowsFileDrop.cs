using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Concurrent;

namespace Stride.Editor.ImGui;

internal sealed class WindowsFileDrop : IDisposable
{
    internal sealed record Drop(string[] Files, int X, int Y);
    private const uint SubclassId = 0x53505249;
    private nint window;
    private readonly SubclassProcedure procedure;
    private readonly ConcurrentQueue<Drop> drops = new();
    private static readonly Dictionary<nint, WindowsFileDrop> owners = [];
    public WindowsFileDrop(nint window)
    {
        if (owners.ContainsKey(window)) throw new InvalidOperationException("The native window already has its file-drop owner.");
        this.window = window; procedure = OnMessage;
        if (!SetWindowSubclass(window, procedure, SubclassId, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        DragAcceptFiles(window, true);
        owners.Add(window, this);
    }
    internal static bool AcceptsOleFiles(nint window) => owners.ContainsKey(window);
    internal static bool DispatchOleFiles(nint window, nint nativeDrop, int screenX, int screenY)
    {
        if (!owners.TryGetValue(window, out var owner)) return false;
        var point = new Point { X = screenX, Y = screenY };
        if (!ScreenToClient(window, ref point)) return false;
        var count = DragQueryFile(nativeDrop, uint.MaxValue, null, 0); var files = new string[count];
        for (uint index = 0; index < count; index++)
        {
            var length = DragQueryFile(nativeDrop, index, null, 0); var text = new StringBuilder(checked((int)length + 1));
            DragQueryFile(nativeDrop, index, text, length + 1); files[index] = text.ToString();
        }
        owner.drops.Enqueue(new(files, point.X, point.Y)); return true;
    }
    public bool TryDequeue(out Drop? drop) => drops.TryDequeue(out drop);
    private nint OnMessage(nint handle, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == 0x0233)
        {
            try
            {
                if (DragQueryPoint(wParam, out var point))
                {
                    var count = DragQueryFile(wParam, uint.MaxValue, null, 0); var files = new string[count];
                    for (uint index = 0; index < count; index++)
                    {
                        var length = DragQueryFile(wParam, index, null, 0); var text = new StringBuilder(checked((int)length + 1));
                        DragQueryFile(wParam, index, text, length + 1); files[index] = text.ToString();
                    }
                    drops.Enqueue(new(files, point.X, point.Y));
                }
            }
            catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
            finally { DragFinish(wParam); }
            return 0;
        }
        return DefSubclassProc(handle, message, wParam, lParam);
    }
    public void Dispose()
    {
        if (window == 0) return;
        if (IsWindow(window))
        {
            DragAcceptFiles(window, false);
            if (!RemoveWindowSubclass(window, procedure, SubclassId)) throw new InvalidOperationException("Could not release the editor's native file drop callback.");
        }
        owners.Remove(window); window = 0;
        while (drops.TryDequeue(out _)) { }
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProcedure(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassProcedure callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProcedure callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ScreenToClient(nint window, ref Point point);
    [DllImport("shell32.dll")] private static extern void DragAcceptFiles(nint window, [MarshalAs(UnmanagedType.Bool)] bool accept);
    [DllImport("shell32.dll", EntryPoint = "DragQueryFileW", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(nint drop, uint index, StringBuilder? text, uint capacity);
    [DllImport("shell32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DragQueryPoint(nint drop, out Point point);
    [DllImport("shell32.dll")] private static extern void DragFinish(nint drop);
}
