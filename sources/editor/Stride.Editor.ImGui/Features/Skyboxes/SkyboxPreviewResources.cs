using Stride.Core.Serialization.Contents;
using Stride.Graphics;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Editor.ImGui;

/// <summary>Actual skybox view, generated sphere and private native loading ownership.</summary>
internal sealed class SkyboxPreviewResources : IDisposable, IMaterialPreviewContentOwner, IRuntimeTextureConsumer
{
    public bool UsesRuntimeTexture(Texture texture) => View?.UsesRuntimeTexture(texture) == true
        || MaterialResources?.UsesRuntimeTexture(texture) == true || CompiledTextureConsumers.UsesTexture(cache, texture)
        || failedScenes.OfType<IRuntimeTextureConsumer>().Any(value => value.UsesRuntimeTexture(texture));
    public CompiledProjectContent SourceContent { get; }
    private IDisposable? contentLease;
    private ContentManager? cache;
    private readonly List<Buffer> buffers = [];
    private readonly List<IDisposable> failedScenes = [];
    internal ModelAssetPreview? View;
    internal MaterialContentCacheResources? MaterialResources;
    internal SkyboxPreviewResources(CompiledProjectContent source)
    { SourceContent = source; contentLease = source.AcquireLease(); }
    internal ContentManager CreateCache() => cache = SourceContent.CreateObjectCache();
    internal void RetainBuffer(Buffer buffer) => buffers.Add(buffer);
    internal void RetainFailedScene(IDisposable scene) => failedScenes.Add(scene);
    public void Dispose()
    {
        if (View != null) { View.Visible = false; View.Dispose(); View = null; }
        while (failedScenes.Count != 0) { failedScenes[^1].Dispose(); failedScenes.RemoveAt(failedScenes.Count - 1); }
        while (buffers.Count != 0) { buffers[^1].Dispose(); buffers.RemoveAt(buffers.Count - 1); }
        if (MaterialResources != null) { MaterialResources.Dispose(); MaterialResources = null; }
        if (cache != null) { cache.UnloadAll(); cache = null; }
        if (contentLease != null) { contentLease.Dispose(); contentLease = null; }
    }
}
