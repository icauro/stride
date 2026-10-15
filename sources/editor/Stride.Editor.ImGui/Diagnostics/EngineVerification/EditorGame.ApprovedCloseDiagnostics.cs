using Stride.Graphics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool approvedCloseDiagnosticCapture;
    private void PrepareApprovedCloseDiagnosticCapture()
    {
        if (workflowTest != null) approvedCloseDiagnosticCapture = true;
    }
    private void SaveApprovedCloseDiagnosticCapture(Texture backBuffer)
    {
        if (!approvedCloseDiagnosticCapture) return;
        approvedCloseDiagnosticCapture = false;
        var capture = Array.IndexOf(args, "--capture");
        if (capture < 0 || capture + 1 >= args.Length) return;
        var path = Path.GetFullPath(args[capture + 1]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // This readback occurs in the approval frame BEFORE the terminal marker,
        // never by drawing a new frame after the terminal certificate.
        SaveWorkflowRenderedCapture(backBuffer);
    }
    private void PublishApprovedCloseDiagnosticResult()
    {
        if (workflowTest == null) return;
        EnsureSceneCpuRetirementCompleted(); EnsureEditorGpuRetirementCompleted(); EnsureInstalledFontGraphicsRetired();
        workflowTest.WriteResult();
        CompleteDiagnosticResult(workflowTest.Finished && workflowTest.Passed,
            workflowTest.Finished ? workflowTest.Result : "Native close completed without a finished workflow assertion result.");
        // The normal close already completed both native barriers. Do not start a
        // diagnostic close actor or invoke CloseApproved a second time.
    }
}
