using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Stride.Editor.ImGui;

internal static partial class WindowsBackgroundLaunch
{
    // A thread-local CBT guard prevents programmatic activation as well as mouse
    // activation. It never activates another application or modifies its input.
    private sealed class BackgroundRootActivationGuard : IDisposable
    {
        private static readonly List<BackgroundRootActivationGuard> failedHookOwners = [];
        private readonly nint owner;
        private readonly uint thread;
        private readonly CbtCallback callback;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly List<object> attempts = [];
        private nint hook;
        private int blocked;
        private bool unhooked;

        internal BackgroundRootActivationGuard(nint owner)
        {
            this.owner = owner;
            thread = GetActivationThreadId();
            callback = Observe;
            hook = InstallActivationHook(5, callback, 0, thread); // WH_CBT
            if (hook == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not protect the background editor from activation.");
            try { WriteAudit(); }
            catch (Exception failure)
            {
                if (!RemoveActivationHook(hook))
                {
                    var retirement = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    failedHookOwners.Add(this);
                    throw new AggregateException("Background activation guard initialization and retirement failed.", failure, retirement);
                }
                hook = 0;
                throw;
            }
        }

        private nint Observe(int code, nint window, nint detail)
        {
            if (code == 5 && window == owner) // HCBT_ACTIVATE
            {
                blocked++;
                // No disk IO or throwing across the native hook boundary.
                try
                {
                    if (attempts.Count < 128)
                        attempts.Add(new { elapsedSeconds = clock.Elapsed.TotalSeconds, hwnd = (long)window,
                            foreground = (long)ReadActivationForegroundWindow() });
                }
                catch { }
                return 1;
            }
            return NextActivationHook(hook, code, window, detail);
        }

        public void Dispose()
        {
            if (hook == 0) return;
            if (!RemoveActivationHook(hook))
            {
                var failure = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not retire the background editor activation guard.");
                // Native callbacks remain rooted if retirement fails. The
                // exception keeps this a failed run rather than a cleanup pass.
                failedHookOwners.Add(this);
                throw failure;
            }
            hook = 0; unhooked = true;
            WriteAudit();
            GC.KeepAlive(callback);
        }

        private void WriteAudit()
        {
            var root = Environment.GetEnvironmentVariable("STRIDE_EDITOR_TEST_ARTIFACTS");
            if (string.IsNullOrEmpty(root)) return;
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "background-root-activation.json"), JsonSerializer.Serialize(new
            {
                schema = 1, process = Environment.ProcessId, hwnd = (long)owner, thread,
                installed = true, unhooked, blockedActivations = blocked, attempts,
                scope = "Exact root HWND, thread-local HCBT_ACTIVATE prevention; no foreground restoration or physical input."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint CbtCallback(int code, nint window, nint detail);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint InstallActivationHook(int kind, CbtCallback callback, nint module, uint thread);
    [DllImport("user32.dll", EntryPoint = "UnhookWindowsHookEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveActivationHook(nint hook);
    [DllImport("user32.dll", EntryPoint = "CallNextHookEx")]
    private static extern nint NextActivationHook(nint hook, int code, nint window, nint detail);
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern nint ReadActivationForegroundWindow();
    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    private static extern uint GetActivationThreadId();
}
