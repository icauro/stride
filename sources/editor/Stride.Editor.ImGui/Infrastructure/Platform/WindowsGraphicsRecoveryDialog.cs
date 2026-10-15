using System.Runtime.InteropServices;

namespace Stride.Editor.ImGui;

internal static class WindowsGraphicsRecoveryDialog
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(nint owner, string text, string title, uint flags);
    internal static int ConfirmClose(nint owner, string? error)
        => ShowOwned(owner, (error == null ? "" : error + "\n\n") + "Save unsaved documents before closing?", 0x3 | 0x30);
    internal static void ReportFailure(nint owner, Exception error)
        => ShowOwned(owner, error.Message, 0x10);
    private static int ShowOwned(nint owner, string message, uint flags)
    {
        using var protection = WindowsBackgroundDialogScope.Begin(owner);
        var result = MessageBox(owner, message, "Stride", flags);
        protection?.Verify();
        return result;
    }
}
