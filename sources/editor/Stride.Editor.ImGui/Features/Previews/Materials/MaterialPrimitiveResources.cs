using Stride.Core.Serialization.Contents;
using Stride.Graphics;
using Stride.Rendering;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Editor.ImGui;

internal interface IMaterialPreviewContentOwner
{
    CompiledProjectContent SourceContent { get; }
}

/// <summary>Actual staged primitive, scene and material-reference ownership, retired under the host GPU certificate.</summary>
internal sealed class MaterialPrimitiveResources(CompiledProjectContent source, ContentManager content) : IDisposable, IMaterialPreviewContentOwner, IRuntimeTextureConsumer
{
    private bool disposed;
    public bool UsesRuntimeTexture(Texture texture) => !disposed && (View?.UsesRuntimeTexture(texture) == true
        || CompiledTextureConsumers.UsesTexture(Model, texture) || CompiledTextureConsumers.UsesTexture(materialReference, texture)
        || CompiledTextureConsumers.UsesTexture(content, texture)
        || failedInitialization.OfType<IRuntimeTextureConsumer>().Any(value => value.UsesRuntimeTexture(texture)));
    public CompiledProjectContent SourceContent { get; } = source;
    internal readonly Model Model = new();
    internal ModelAssetPreview? View;
    private readonly List<Buffer> buffers = [];
    private readonly List<IDisposable> failedInitialization = [];
    private Material? materialReference;
    private IDisposable? contentLease = source.AcquireLease();

    internal void RetainMaterial(Material material)
    {
        if (!content.TryGetAssetUrl(material, out var url))
            throw new InvalidOperationException("The material preview is not retained by its actual content cache.");
        materialReference = content.Load<Material>(url, ContentManagerLoaderSettings.StreamingDisabled);
        if (!ReferenceEquals(materialReference, material))
            throw new InvalidOperationException("The primitive material reference changed during staging.");
    }
    internal void RetainBuffer(Buffer buffer)
    {
        foreach (var current in buffers) if (ReferenceEquals(current, buffer)) return;
        buffers.Add(buffer);
    }
    internal void RetainFailedInitialization(IDisposable resource) => failedInitialization.Add(resource);
    public void Dispose()
    {
        if (View != null) { View.Visible = false; View.Dispose(); View = null; }
        while (failedInitialization.Count != 0)
        { failedInitialization[^1].Dispose(); failedInitialization.RemoveAt(failedInitialization.Count - 1); }
        while (buffers.Count != 0)
        { buffers[^1].Dispose(); buffers.RemoveAt(buffers.Count - 1); }
        if (materialReference != null)
        { content.Unload(materialReference); materialReference = null; }
        if (contentLease != null) { contentLease.Dispose(); contentLease = null; }
        disposed = true;
    }
}
