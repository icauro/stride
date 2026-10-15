using System.Runtime.InteropServices;

namespace Stride.Editor.ImGui;

internal static class WindowsStartupDialog
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(nint owner, string text, string title, uint flags);

    internal static bool ConfirmGraphicsFallback(string requested, string available)
        => MessageBox(0, $"Graphics API '{requested}' (from Game Studio settings) is not available in this build.\n\nContinue with '{available}' and reset the setting to Default?",
            "Stride", 0x4 | 0x30) == 6; // MB_YESNO | MB_ICONWARNING, IDYES
}
