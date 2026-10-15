using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class UIEditorPanel
{
    private bool cameraPanning;
    private void StopCameraInput()
    {
        host.EndCameraCapture();
        cameraPanning = false;
    }
    private void DrawCameraInput(UIDocument source, NVector2 size, bool hovered)
    {
        var io = Gui.GetIO();
        if (!Gui.IsMouseDown(ImGuiMouseButton.Middle) || io.WantTextInput || Gui.IsKeyPressed(ImGuiKey.Escape, false)) StopCameraInput();
        if (hovered && Gui.IsMouseClicked(ImGuiMouseButton.Middle)) cameraPanning = host.BeginCameraCapture();
        var motion = NVector2.Zero;
        if (cameraPanning)
        {
            if (host.ConsumeCameraMotion() is { } raw) motion = raw / size;
            else StopCameraInput();
        }
        var settings = host.CameraMotionSettings(source);
        viewport!.ApplyCameraInput(motion, hovered && !io.WantTextInput && !Gui.IsMouseDown(ImGuiMouseButton.Middle) ? io.MouseWheel : 0,
            cameraPanning, io.KeyShift, settings.Speed, settings.SceneUnit);
    }
}
