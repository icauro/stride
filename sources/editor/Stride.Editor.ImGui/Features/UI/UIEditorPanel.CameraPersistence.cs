namespace Stride.Editor.ImGui;

internal sealed partial class UIEditorPanel
{
    internal void ReleaseDocumentView(UIDocument source)
    {
        if (!ReferenceEquals(document, source)) { uiViewSettings.Remove(source); return; }
        SaveCameraView(throwOnFailure: true);
        Dispose();
    }
    private void SaveCameraView(bool throwOnFailure = false)
    {
        if (document == null) return;
        var view = viewport?.CaptureCameraView() ?? restoreView;
        if (view == null) return;
        try { host.SaveCameraView(document, view); }
        catch (Exception error)
        {
            if (throwOnFailure) throw;
            host.ReportValidationFailure(error);
        }
    }
}
