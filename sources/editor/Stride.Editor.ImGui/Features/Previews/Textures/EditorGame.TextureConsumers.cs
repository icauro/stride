using Stride.Graphics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool TextureHasConsumers(Texture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (SnapshotSceneResourceViews(includeRetiring: true).Any(view => view.Viewport.UsesRuntimeTexture(texture, view.Document))) return true;
        if (modelPreview?.UsesRuntimeTexture(texture) == true || materialPreview?.UsesRuntimeTexture(texture) == true
            || animationPreview?.UsesRuntimeTexture(texture) == true || skyboxPreview?.UsesRuntimeTexture(texture) == true
            || texturePreview?.UsesRuntimeTexture(texture) == true || spriteSheetPreview?.UsesRuntimeTexture(texture) == true
            || fontPreview?.UsesRuntimeTexture(texture) == true || catalogPreviewPanel?.UsesRuntimeTexture(texture) == true) return true;
        if (ownedUIEditors.Values.Any(view => view.UsesRuntimeTexture(texture))
            || spriteCanvases.Values.Any(state => state.Preview?.UsesRuntimeTexture(texture) == true)) return true;
        if (retiredSkyboxResources.Any(owner => owner.Resource is IRuntimeTextureConsumer consumer && consumer.UsesRuntimeTexture(texture))) return true;
        // Queued previews still own their real CPU/cache references after a GPU
        // certificate; only their completed physical disposal ends consumption.
        return retiredEditorPreviews.Any(owner => !owner.PreviewDisposed && owner.Preview is IRuntimeTextureConsumer consumer
            && consumer.UsesRuntimeTexture(texture));
    }
}
