using Stride.Graphics;

namespace Stride.Editor.ImGui;

internal sealed partial class ModelAssetPreview
{
    public bool UsesRuntimeTexture(Texture texture)
    {
        if (IsDisposed) return false;
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return CompiledTextureConsumers.UsesTexture(model, texture, visited)
            || CompiledTextureConsumers.UsesTexture(modelEntity, texture, visited)
            || CompiledTextureConsumers.UsesTexture(sceneSystem?.SceneInstance?.RootScene, texture, visited)
            || CompiledTextureConsumers.UsesTexture(content, texture, visited);
    }
}
