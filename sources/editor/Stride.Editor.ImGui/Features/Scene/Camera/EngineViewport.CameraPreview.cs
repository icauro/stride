using Stride.Assets;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering.Compositing;

namespace Stride.Editor.ImGui;

internal sealed partial class EngineViewport
{
    private SceneCameraPreviewRenderer? cameraPreview;
    internal bool ShowCameraPreview { get; set; } = true;
    internal SceneCameraPreviewRenderer? CameraPreview => cameraPreview;
    internal Action<Exception>? CameraPreviewFailure { get; set; }
    private void InitializeCameraPreview()
    {
        if (loadedCompositor?.Game is not EditorTopLevelCompositor top || top.Child == null) return;
        cameraPreview = new SceneCameraPreviewRenderer(game, top.Child, SelectedRuntimeCamera,
            () => ShowCameraPreview && !top.EnablePreviewGame, SceneGameAspect, error =>
            {
                if (CameraPreviewFailure != null) CameraPreviewFailure(error); else Console.Error.WriteLine(error);
            });
        top.PostGizmoCompositors.Add(cameraPreview);
    }
    private CameraComponent? SelectedRuntimeCamera()
    {
        return selectedWorldRoots.Count == 1 ? selectedWorldRoots[0].Get<CameraComponent>() : null;
    }
    private void ClearCameraPreview(SceneViewportResources? retirement = null)
    {
        if (cameraPreview == null) return;
        if (loadedCompositor?.Game is EditorTopLevelCompositor top) top.PostGizmoCompositors.Remove(cameraPreview);
        if (retirement != null) retirement.Retain(cameraPreview); else cameraPreview.Dispose();
        cameraPreview = null;
    }
}
