using Hexa.NET.ImGui;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;
using SVector2 = Stride.Core.Mathematics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private AssetPreviewCamera? previewCameraCaptureOwner;
    private int previewCameraInputFrame = -1;
    private readonly ref struct AssetPreviewCameraFrameScope(EditorGame editor)
    {
        public void Dispose()
        {
            if (editor.previewCameraInputFrame != Gui.GetFrameCount()) editor.EndAssetPreviewCameraCapture();
        }
    }

    private void EndAssetPreviewCameraCapture()
    {
        if (previewCameraCaptureOwner == null) return;
        platformViewports?.EndCameraCapture(previewCameraCaptureOwner);
        previewCameraCaptureOwner = null;
    }

    private void DrawAssetPreviewCameraInput(string id, NVector2 size, AssetPreviewCamera camera)
    {
        previewCameraInputFrame = Gui.GetFrameCount();
        EditorPreviewPointerInput.Draw(id, size);
        if (previewCameraCaptureOwner != null && !ReferenceEquals(previewCameraCaptureOwner, camera))
            EndAssetPreviewCameraCapture();
        var io = Gui.GetIO();
        var down = false;
        for (var button = 0; button < 5; ++button) down |= Gui.IsMouseDown((ImGuiMouseButton)button);
        var active = Gui.IsItemActive() && down && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)
            && !io.WantTextInput && !IsEditorGraphicsRecovering && !IsEditorGpuRetirementPending && !IsEditorGpuRetirementFinalizing;
        var captured = platformViewports?.IsCameraCaptureOwned(camera) == true;
        if (!active)
        {
            if (ReferenceEquals(previewCameraCaptureOwner, camera)) EndAssetPreviewCameraCapture();
        }
        else if (!captured && Gui.IsItemActivated())
        {
            captured = platformViewports?.BeginCameraCapture(Gui.GetWindowViewport().ID, camera) == true;
            if (captured) previewCameraCaptureOwner = camera;
        }
        var motion = captured && active ? platformViewports!.ConsumeCameraMotion(camera) : active ? io.MouseDelta : NVector2.Zero;
        if (Gui.IsItemActivated()) motion = NVector2.Zero;
        var wheel = Gui.IsItemHovered() ? io.MouseWheel : 0;
        if (active || wheel != 0)
            camera.Navigate(new SVector2(motion.X / size.X, motion.Y / size.Y),
                active && Gui.IsMouseDown(ImGuiMouseButton.Right), active && Gui.IsMouseDown(ImGuiMouseButton.Left), wheel);
    }
}
