using Stride.Core.Serialization.Contents;

namespace Stride.Editor.ImGui;

/// <summary>An isolated material pipeline cache, including assets loaded before generation or loading fails.</summary>
internal sealed class MaterialContentCacheResources : IDisposable, IMaterialPreviewContentOwner, IRuntimeTextureConsumer
{
    public bool UsesRuntimeTexture(Stride.Graphics.Texture texture) => CompiledTextureConsumers.UsesTexture(Content, texture);
    public CompiledProjectContent SourceContent { get; }
    internal ContentManager Content { get; }
    private IDisposable? contentLease;
    internal MaterialContentCacheResources(CompiledProjectContent source)
    {
        SourceContent = source; contentLease = source.AcquireLease();
        try { Content = source.CreateObjectCache(); }
        catch { contentLease.Dispose(); throw; }
    }
    internal IDisposable TransferLease()
    {
        var lease = contentLease ?? throw new InvalidOperationException("The material cache ownership has already transferred.");
        contentLease = null; return lease;
    }
    public void Dispose()
    {
        Content.UnloadAll();
        if (contentLease != null) { contentLease.Dispose(); contentLease = null; }
    }
}
