using System.Runtime.InteropServices;
using System.Windows.Forms;
using Stride.Games;

namespace Stride.Editor.ImGui;

// Explicit automation launch policy. Actual native focus reporting stays intact.
internal static partial class WindowsBackgroundLaunch
{
    internal static bool Enabled => Environment.GetCommandLineArgs().Contains("--background-launch", StringComparer.Ordinal);
    internal static GameContext CreateContext()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Background launch requires the Windows host.");
        return new GameContextWinforms(new BackgroundGameForm());
    }
    private sealed class BackgroundGameForm : GameForm
    {
        private BackgroundRootActivationGuard? activationGuard;
        protected override bool ShowWithoutActivation => true;
        protected override void OnHandleCreated(EventArgs args)
        {
            base.OnHandleCreated(args);
            activationGuard = new BackgroundRootActivationGuard(Handle);
        }
        protected override void OnHandleDestroyed(EventArgs args)
        {
            try { activationGuard?.Dispose(); }
            finally { activationGuard = null; base.OnHandleDestroyed(args); }
        }
        protected override void WndProc(ref Message message)
        {
            // Match the detached viewport policy for the main automation host.
            if (message.Msg == 0x0021) // WM_MOUSEACTIVATE
            {
                message.Result = new nint(3); // MA_NOACTIVATE
                return;
            }
            base.WndProc(ref message);
        }
        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(value);
            if (value && IsHandleCreated)
                PlaceBehindOtherWindows(Handle);
        }
        protected override CreateParams CreateParams
        {
            get { var parameters = base.CreateParams; parameters.ExStyle |= 0x08000000; return parameters; }
        }
    }
    internal static void PlaceBehindOtherWindows(nint window)
    {
        // HWND_BOTTOM, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE.
        // A background test window must neither activate nor cover the user's
        // foreground application. Keep the normal interactive host unchanged.
        if (!SetWindowPos(window, new nint(1), 0, 0, 0, 0, 0x13))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not show the editor behind other windows.");
    }
    internal static void SetBounds(nint window, int x, int y, int width, int height)
    {
        // SWP_NOZORDER | SWP_NOACTIVATE. No focus, activation or cursor operation.
        if (!SetWindowPos(window, 0, x, y, width, height, 0x14))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not position the background editor window.");
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
