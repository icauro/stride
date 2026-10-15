using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Stride.Editor.ImGui;

// Thread-local lifetime around the real COM Show call; no desktop focus restoration.
internal sealed class WindowsBackgroundDialogScope : IDisposable
{
    internal sealed record Receipt(uint NativeThreadId, long OwnerHandle, bool Installed, int ProtectedCreations,
        int PreventedActivations, bool Verified, bool Unhooked, string? Failure);
    [ThreadStatic] private static Receipt? lastReceipt;
    internal static Receipt? LastReceipt => lastReceipt;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint HookProc(int code, nuint window, nint data);
    [StructLayout(LayoutKind.Sequential)]
    private struct CreateWindow { internal nint Parameters, Instance, Menu, Parent; internal int Height, Width, Y, X, Style; internal nint Name, Class; internal uint ExtendedStyle; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CreateHook { internal nint Parameters, InsertAfter; }
    private static readonly List<WindowsBackgroundDialogScope> failedUnhooks = [];
    private readonly nint owner;
    private readonly uint thread;
    private readonly HookProc callback;
    private readonly HashSet<nint> dialogs = [];
    private nint hook;
    private Exception? failure;
    private bool verified;
    internal int ProtectedCreations { get; private set; }
    internal int PreventedActivations { get; private set; }
    private WindowsBackgroundDialogScope(nint owner)
    {
        this.owner = owner; thread = GetCurrentThreadId(); callback = Observe;
        if (owner == 0 || GetWindowThreadProcessId(owner, out var process) != thread || process != Environment.ProcessId)
            throw new InvalidOperationException("Background dialogs require an actual owner on their current native thread.");
        hook = SetWindowsHookEx(5, callback, 0, thread); // WH_CBT, current thread only.
        if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not protect background dialog creation.");
        lastReceipt = new(thread, owner.ToInt64(), true, 0, 0, false, false, null);
    }
    internal static WindowsBackgroundDialogScope? Begin(nint owner)
        => WindowsBackgroundLaunch.Enabled ? new(owner) : null;
    private nint Observe(int code, nuint value, nint data)
    {
        if (code < 0) return CallNextHookEx(hook, code, value, data);
        var window = (nint)value;
        try
        {
            if (GetCurrentThreadId() != thread) throw new InvalidOperationException("Background dialog hook changed native threads.");
            if (code == 3 && data != 0) // HCBT_CREATEWND, before WM_NCCREATE/WM_CREATE.
            {
                var creation = Marshal.PtrToStructure<CreateHook>(data);
                var parameters = Marshal.PtrToStructure<CreateWindow>(creation.Parameters);
                var isDialog = parameters.Class == (nint)32770 || parameters.Class.ToInt64() > 65535
                    && Marshal.PtrToStringUni(parameters.Class) == "#32770";
                if ((parameters.Style & 0x40000000) == 0 && isDialog
                    && (parameters.Parent == owner || parameters.Parent == 0 || dialogs.Contains(parameters.Parent)))
                {
                    if (GetWindowThreadProcessId(window, out var nativeProcess) != thread || nativeProcess != Environment.ProcessId)
                        throw new InvalidOperationException("The precreation dialog HWND does not belong to its actual owner thread and process.");
                    const int extendedStyleIndex = -20;
                    Marshal.SetLastPInvokeError(0);
                    var previousStyle = GetWindowLongPtr(window, extendedStyleIndex);
                    var nativeError = Marshal.GetLastPInvokeError();
                    if (previousStyle == 0 && nativeError != 0) throw new Win32Exception(nativeError, "Could not read the precreation dialog style.");
                    var desiredStyle = (nint)(previousStyle.ToInt64() | 0x08000000);
                    Marshal.SetLastPInvokeError(0);
                    var replacedStyle = SetWindowLongPtr(window, extendedStyleIndex, desiredStyle);
                    nativeError = Marshal.GetLastPInvokeError();
                    if (replacedStyle == 0 && nativeError != 0) throw new Win32Exception(nativeError, "Could not protect the actual precreation dialog HWND.");
                    Marshal.SetLastPInvokeError(0);
                    var installedStyle = GetWindowLongPtr(window, extendedStyleIndex);
                    nativeError = Marshal.GetLastPInvokeError();
                    if (installedStyle == 0 && nativeError != 0 || installedStyle != desiredStyle)
                        throw new Win32Exception(nativeError, "The actual precreation dialog HWND did not retain its no-activation style.");
                    parameters.ExtendedStyle |= 0x08000000; // WS_EX_NOACTIVATE before first Show.
                    creation.InsertAfter = (nint)1; // HWND_BOTTOM before initial insertion.
                    Marshal.StructureToPtr(parameters, creation.Parameters, false);
                    Marshal.StructureToPtr(creation, data, false);
                    dialogs.Add(window); ProtectedCreations++;
                }
            }
            if (code == 5 && (window == owner || dialogs.Contains(window))) // HCBT_ACTIVATE
            { PreventedActivations++; return 1; }
        }
        catch (Exception error) { failure ??= error; return 1; }
        return CallNextHookEx(hook, code, value, data);
    }
    internal void Verify()
    {
        if (failure != null) throw new InvalidOperationException("Background dialog protection failed.", failure);
        if (ProtectedCreations == 0) throw new InvalidOperationException("The real native dialog was not observed before its creation.");
        verified = true;
    }
    public void Dispose()
    {
        if (hook == 0) return;
        if (GetCurrentThreadId() != thread) throw new InvalidOperationException("Background dialog hook must retire on its native owner thread.");
        if (!UnhookWindowsHookEx(hook))
        {
            lastReceipt = new(thread, owner.ToInt64(), true, ProtectedCreations, PreventedActivations, verified, false, "Native hook retirement failed.");
            lock (failedUnhooks) failedUnhooks.Add(this); // Keep delegate alive if native hook remains installed.
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Background dialog hook retirement failed.");
        }
        hook = 0; GC.KeepAlive(callback);
        lastReceipt = new(thread, owner.ToInt64(), true, ProtectedCreations, PreventedActivations, verified, true, failure?.ToString());
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static extern nint SetWindowsHookEx(int type, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint window, nint data);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}
