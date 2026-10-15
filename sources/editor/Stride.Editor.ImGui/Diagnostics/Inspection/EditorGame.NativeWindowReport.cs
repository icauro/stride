using System.Text.Json;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool nativeWindowReportWritten;
    private bool nativeDialogTestStarted;
    private void PumpNativeDialogCancellationTest()
    {
        var argument = Array.IndexOf(args, "--native-dialog-cancel-test");
        if (argument < 0 || nativeDialogTestStarted || !nativeWindowReportWritten) return;
        if (Array.IndexOf(args, "--native-window-report") < 0 || argument + 1 >= args.Length)
            throw new ArgumentException("Native dialog test requires result and native window report paths");
        nativeDialogTestStarted = true;
        var owner = (nint)Window.NativeWindow.Handle;
        string? selected = null;
        string? error = null;
        try
        {
            selected = WindowsFileDialogs.Open(owner, "Open Stride project or solution",
                "Stride packages\0*.sdpkg\0All files\0*.*\0\0", Path.GetTempPath(), null);
        }
        catch (Exception failure) { error = failure.ToString(); }
        var path = Path.GetFullPath(args[argument + 1]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, processId = Environment.ProcessId, ownerHandle = owner.ToInt64(),
            selectedPath = selected, canceled = selected == null && error == null,
            dialogReturned = true, error,
            backgroundGuard = WindowsBackgroundDialogScope.LastReceipt,
        }));
    }
    private void WriteNativeWindowTestReport()
    {
        if (nativeWindowReportWritten) return;
        var argument = Array.IndexOf(args, "--native-window-report");
        if (argument < 0) { nativeWindowReportWritten = true; return; }
        if (argument + 1 >= args.Length) throw new ArgumentException("--native-window-report requires a path");
        var path = Path.GetFullPath(args[argument + 1]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, processId = Environment.ProcessId,
            handle = ((nint)Window.NativeWindow.Handle).ToInt64(),
            drawCompleted = true, frame = frames,
        }));
        nativeWindowReportWritten = true;
    }
}
