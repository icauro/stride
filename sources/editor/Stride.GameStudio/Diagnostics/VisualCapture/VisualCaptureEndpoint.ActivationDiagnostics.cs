using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Stride.GameStudio.Diagnostics.VisualCapture;

internal sealed partial class VisualCaptureEndpoint
{
    private const int ActivationRecordLimit = 512;
    private bool activationTraceEnabled, activationTraceDisposed;
    private int activationTraceRecords;
    private string activationTracePath;
    private Window activationTraceWindow;
    private HwndSource activationTraceSource;
    private IntPtr activationShowHook, activationForegroundHook;
    private ActivationWinEventProc activationNativeCallback;
    private Application activationTraceApplication;
    private InputManager activationTraceInputManager;
    // Only failed native unhooks retain the endpoint/delegate until a later
    // cleanup attempt succeeds; dropping that root could invoke a collected delegate.
    private static readonly HashSet<VisualCaptureEndpoint> activationUnhookRoots = [];

    private void StartMatchedActivationDiagnostics()
    {
        try { StartMatchedActivationDiagnosticsCore(); }
        catch (Exception error)
        {
            WriteActivation("instrumentation-initialization-failed", new { error = error.ToString() });
            StopMatchedActivationDiagnostics();
        }
    }
    private void StartMatchedActivationDiagnosticsCore()
    {
        if (!Environment.GetCommandLineArgs().Contains("--matched-benchmark")
            || Environment.GetEnvironmentVariable("STRIDE_EDITOR_BENCHMARK_ACTIVATION_TRACE") != "1") return;
        activationTracePath = Path.Combine(directory, "matched-activation.jsonl");
        activationTraceEnabled = true;
        activationTraceApplication = Application.Current ?? throw new InvalidOperationException("Activation tracing requires the actual live WPF application.");
        activationTraceInputManager = InputManager.Current;
        WriteActivation("instrumentation-start", new { limit = ActivationRecordLimit,
            scope = "Main HWND native activation/focus messages and actual WPF activation/keyboard-focus events; event callback stacks are not proof of originating API caller",
            timingImpact = "Explicit diagnostic event handlers, stack capture and synchronous JSONL writes; use untraced runs for accepted performance timing" });
        activationNativeCallback = ActivationNativeEvent; // Strong root until every native hook is removed.
        activationShowHook = ActivationSetWinEventHook(0x8002, 0x8002, IntPtr.Zero, activationNativeCallback, (uint)Environment.ProcessId, 0, 0);
        activationForegroundHook = ActivationSetWinEventHook(3, 3, IntPtr.Zero, activationNativeCallback, (uint)Environment.ProcessId, 0, 0);
        if (activationShowHook == IntPtr.Zero || activationForegroundHook == IntPtr.Zero)
            WriteActivation("hook-unavailable", new { win32Error = Marshal.GetLastWin32Error(),
                showHookAvailable = activationShowHook != IntPtr.Zero, foregroundHookAvailable = activationForegroundHook != IntPtr.Zero });
        activationTraceApplication.Activated += ActivationApplicationEvent;
        activationTraceApplication.Deactivated += ActivationApplicationEvent;
        activationTraceApplication.Exit += ActivationApplicationExit;
        activationTraceInputManager.PreProcessInput += ActivationKeyboardFocus;
        dispatcher.ShutdownStarted += ActivationDispatcherShutdown;
        AttachActivationMainWindow();
    }
    private void AttachActivationMainWindow()
    {
        if (activationTraceDisposed || !activationTraceEnabled) return;
        var main = activationTraceApplication?.MainWindow;
        if (main == null) return;
        if (activationTraceWindow != null && !ReferenceEquals(main, activationTraceWindow))
        {
            WriteActivation("main-owner-replaced", new { previousType = activationTraceWindow.GetType().FullName,
                nextType = main.GetType().FullName, nextRole = ActivationRole(main) });
            DetachActivationWindow();
        }
        if (activationTraceWindow == null)
        {
            activationTraceWindow = main;
            main.SourceInitialized += ActivationSourceInitialized;
            main.Activated += ActivationWindowActivated;
            main.Deactivated += ActivationWindowActivated;
        }
        if (!ReferenceEquals(main, activationTraceWindow) || activationTraceSource != null) return;
        var handle = new WindowInteropHelper(main).Handle;
        if (handle == IntPtr.Zero) return;
        var source = HwndSource.FromHwnd(handle);
        if (source == null) return;
        activationTraceSource = source;
        source.AddHook(ActivationWindowMessage);
        WriteActivation("main-hook-attached", new { handle = CaptureNativeBounds.Handle(handle), main.ShowActivated,
            ownerType = main.GetType().FullName, ownerRole = ActivationRole(main),
            windowState = main.WindowState.ToString(), eventBoundary = "Native main source existed at attachment; earlier messages are not claimed" });
    }
    private void ActivationSourceInitialized(object sender, EventArgs args) => AttachActivationMainWindow();
    private void ActivationApplicationEvent(object sender, EventArgs args)
    {
        if (activationTraceDisposed) return;
        AttachActivationMainWindow();
        WriteActivation("wpf-application-activation", new { mainIsActive = activationTraceApplication?.MainWindow?.IsActive });
    }
    private void ActivationWindowActivated(object sender, EventArgs args)
    {
        if (activationTraceDisposed) return;
        WriteActivation("wpf-window-activation", new { active = activationTraceWindow?.IsActive });
    }
    private void ActivationKeyboardFocus(object sender, PreProcessInputEventArgs args)
    {
        if (activationTraceDisposed || args.StagingItem.Input is not KeyboardFocusChangedEventArgs change) return;
        var main = activationTraceApplication?.MainWindow;
        var oldOwner = change.OldFocus is DependencyObject oldValue ? Window.GetWindow(oldValue) : null;
        var newOwner = change.NewFocus is DependencyObject newValue ? Window.GetWindow(newValue) : null;
        if (main == null || !ReferenceEquals(main, oldOwner) && !ReferenceEquals(main, newOwner)) return;
        AttachActivationMainWindow();
        WriteActivation("wpf-keyboard-focus", new { routedEvent = change.RoutedEvent.Name,
            oldType = change.OldFocus?.GetType().FullName, newType = change.NewFocus?.GetType().FullName,
            oldOwnerIsMain = ReferenceEquals(main, oldOwner), newOwnerIsMain = ReferenceEquals(main, newOwner) });
    }
    private IntPtr ActivationWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!activationTraceDisposed && message is 0x0006 or 0x0021 or 0x0007 or 0x0008 or 0x001C)
            WriteActivation("native-window-message", new { hwnd = CaptureNativeBounds.Handle(hwnd), message = "0x" + message.ToString("x"),
                wParam = wParam.ToInt64(), lParam = CaptureNativeBounds.Handle(lParam), handledBefore = handled });
        // Observational only: neither handled nor native message return semantics change.
        return IntPtr.Zero;
    }
    private void ActivationNativeEvent(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint nativeThread, uint nativeMilliseconds)
    {
        if (activationTraceDisposed) return;
        void Record()
        {
            if (activationTraceDisposed) return; // Queued callbacks cannot resurrect disposed hooks.
            AttachActivationMainWindow();
            var mainHandle = activationTraceWindow == null ? IntPtr.Zero : new WindowInteropHelper(activationTraceWindow).Handle;
            if (hwnd != mainHandle || mainHandle == IntPtr.Zero) return;
            WriteActivation("native-main-event", new { eventType = "0x" + eventType.ToString("x"), objectId, childId, nativeThread,
                nativeMilliseconds, hwnd = CaptureNativeBounds.Handle(hwnd), attribution = "Native event observation; managed stack identifies callback dispatch only" });
        }
        if (dispatcher.CheckAccess()) Record();
        else if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke((Action)Record);
    }
    private void WriteActivation(string kind, object details)
    {
        if (!activationTraceEnabled || activationTraceRecords >= ActivationRecordLimit) return;
        var stack = new StackTrace(1, true).ToString();
        if (stack.Length > 8192) stack = stack[..8192];
        var row = JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, monotonicTicks = Stopwatch.GetTimestamp(),
            monotonicFrequency = Stopwatch.Frequency, processId = Environment.ProcessId, managedThread = Environment.CurrentManagedThreadId,
            sequence = ++activationTraceRecords, kind, foreground = CaptureNativeBounds.Handle(ActivationGetForegroundWindow()),
            observedOwnerType = activationTraceWindow?.GetType().FullName, observedOwnerRole = ActivationRole(activationTraceWindow),
            observedOwnerIsCurrentMain = activationTraceWindow != null && ReferenceEquals(activationTraceApplication?.MainWindow, activationTraceWindow),
            observedOwnerHwnd = CaptureNativeBounds.Handle(activationTraceSource?.Handle ?? IntPtr.Zero),
            details, managedCallbackStack = stack, lastAllowedRecord = activationTraceRecords == ActivationRecordLimit });
        try { File.AppendAllText(activationTracePath, row + Environment.NewLine); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { activationTraceEnabled = false; }
    }
    private void StopMatchedActivationDiagnostics()
    {
        if (!activationTraceEnabled && activationNativeCallback == null) return;
        activationTraceDisposed = true;
        var showRemoved = activationShowHook == IntPtr.Zero || ActivationUnhookWinEvent(activationShowHook);
        var foregroundRemoved = activationForegroundHook == IntPtr.Zero || ActivationUnhookWinEvent(activationForegroundHook);
        if (showRemoved) activationShowHook = IntPtr.Zero;
        if (foregroundRemoved) activationForegroundHook = IntPtr.Zero;
        lock (activationUnhookRoots)
        {
            if (showRemoved && foregroundRemoved) activationUnhookRoots.Remove(this);
            else activationUnhookRoots.Add(this);
        }
        if (activationTraceApplication != null)
        {
            activationTraceApplication.Activated -= ActivationApplicationEvent;
            activationTraceApplication.Deactivated -= ActivationApplicationEvent;
            activationTraceApplication.Exit -= ActivationApplicationExit;
        }
        if (activationTraceInputManager != null) activationTraceInputManager.PreProcessInput -= ActivationKeyboardFocus;
        dispatcher.ShutdownStarted -= ActivationDispatcherShutdown;
        DetachActivationWindow();
        WriteActivation("instrumentation-stop", new { showHookRemoved = showRemoved, foregroundHookRemoved = foregroundRemoved,
            delegateRootRetained = !showRemoved || !foregroundRemoved, queuedCallbacksDiscarded = true });
        activationTraceEnabled = false;
        activationTraceApplication = null; activationTraceInputManager = null;
        // Keep the delegate rooted for any already-queued native callbacks; their disposal guard exits.
    }
    private void ActivationApplicationExit(object sender, ExitEventArgs args) => StopMatchedActivationDiagnostics();
    private void ActivationDispatcherShutdown(object sender, EventArgs args) => StopMatchedActivationDiagnostics();
    private void DetachActivationWindow()
    {
        if (activationTraceWindow != null)
        {
            activationTraceWindow.SourceInitialized -= ActivationSourceInitialized;
            activationTraceWindow.Activated -= ActivationWindowActivated;
            activationTraceWindow.Deactivated -= ActivationWindowActivated;
        }
        if (activationTraceSource != null && !activationTraceSource.IsDisposed) activationTraceSource.RemoveHook(ActivationWindowMessage);
        activationTraceSource = null; activationTraceWindow = null;
    }
    private static string ActivationRole(Window window) => window == null ? "none"
        : window is Stride.GameStudio.View.GameStudioWindow ? "editor-main" : "startup-owner-candidate";
    private delegate void ActivationWinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint nativeThread, uint milliseconds);
    [DllImport("user32.dll", EntryPoint = "SetWinEventHook", SetLastError = true)] private static extern IntPtr ActivationSetWinEventHook(uint first, uint last, IntPtr module, ActivationWinEventProc callback, uint pid, uint thread, uint flags);
    [DllImport("user32.dll", EntryPoint = "UnhookWinEvent")] private static extern bool ActivationUnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern IntPtr ActivationGetForegroundWindow();
}
