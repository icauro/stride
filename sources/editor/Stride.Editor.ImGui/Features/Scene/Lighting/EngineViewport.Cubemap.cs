using Stride.Graphics;
using Stride.Rendering.Skyboxes;

namespace Stride.Editor.ImGui;

internal sealed partial class EngineViewport
{
    internal Image CaptureSceneCubemap()
    {
        if (ownedSceneInstance == null || sceneAuthority == null || !ReferenceEquals(sceneAuthority.SceneSystem.SceneInstance, ownedSceneInstance))
            throw new InvalidOperationException("The scene is not active.");
        SuppressNavigationForBake(true);
        try
        {
            sceneAuthority.PrepareCapture(game.DrawTime);
            using var texture = CubemapSceneRenderer.GenerateCubemap(sceneAuthority, Camera.Position, 1024);
            return texture.GetDataAsImage(game.GraphicsContext.CommandList);
        }
        finally { SuppressNavigationForBake(false); }
    }
}
