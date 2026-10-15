using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed unsafe partial class WindowsImGuiViewports
{
    private WindowsRelativeMouseCapture? cameraCapture;
    private bool cameraPreviousDrawCursor;
    private int cameraCaptureFrame;
    private object? cameraCaptureOwner;
    internal bool HasCameraCapture => cameraCapture?.Active == true;
    internal bool IsCameraCaptureOwned(object owner) => HasCameraCapture && ReferenceEquals(cameraCaptureOwner, owner);
    internal Vector2 ConsumeCameraMotion(object owner) => IsCameraCaptureOwned(owner) ? ConsumeCameraMotion() : Vector2.Zero;
    internal void EndCameraCapture(object owner) { if (ReferenceEquals(cameraCaptureOwner, owner)) EndCameraCapture(); }
    internal Vector2 CameraCaptureMotion => cameraCapture?.TotalMotion ?? Vector2.Zero;
    internal Vector2 ConsumeCameraMotion() { cameraCaptureFrame = Gui.GetFrameCount(); return cameraCapture?.ConsumeMotion() ?? Vector2.Zero; }
    internal bool BeginCameraCapture(uint viewportId) => BeginCameraCapture(viewportId, null);
    internal bool BeginCameraCapture(uint viewportId, object? owner)
    {
        if (syntheticInput) return false;
        if (HasCameraCapture) return ReferenceEquals(cameraCaptureOwner, owner);
        EndCameraCapture();
        if (primaryPointer != null || WindowsSourceTextDragDrop.IsDragging || Gui.GetIO().WantTextInput
            || Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)) return false;
        var handle = viewportId == mainViewport ? mainWindow : windows.GetValueOrDefault(viewportId)?.Handle ?? 0;
        if (handle == 0 || GetForegroundWindow() != handle) return false;
        cameraCapture = new WindowsRelativeMouseCapture(handle, reportError);
        cameraPreviousDrawCursor = Gui.GetIO().MouseDrawCursor;
        try { cameraCapture.Start(); cameraCaptureOwner = owner; cameraCaptureFrame = Gui.GetFrameCount(); Gui.GetIO().MouseDrawCursor = false; return true; }
        catch (Exception failure)
        {
            try { EndCameraCapture(); }
            catch (Exception recovery) { throw new AggregateException("Camera pointer capture and restoration failed.", failure, recovery); }
            throw;
        }
    }
    internal void EndCameraCapture()
    {
        if (cameraCapture == null) return;
        cameraCapture.Dispose(); cameraCapture = null; cameraCaptureOwner = null;
        Gui.GetIO().MouseDrawCursor = cameraPreviousDrawCursor;
    }
    private void CheckCameraCaptureOwnership()
    {
        if (cameraCapture != null && (!cameraCapture.Active || GetForegroundWindow() != cameraCapture.Window
            || Gui.GetFrameCount() != cameraCaptureFrame || primaryPointer != null || WindowsSourceTextDragDrop.IsDragging || Gui.GetIO().WantTextInput
            || Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel))) EndCameraCapture();
    }
    private void SubmitCameraPointer(ImGuiIOPtr io)
    {
        if (!HasCameraCapture) return;
        var capture = cameraCapture!; capture.KeepHidden();
        io.AddMousePosEvent(capture.Anchor.X, capture.Anchor.Y);
        io.AddMouseViewportEvent(windowIds.GetValueOrDefault(capture.Window));
    }
}
