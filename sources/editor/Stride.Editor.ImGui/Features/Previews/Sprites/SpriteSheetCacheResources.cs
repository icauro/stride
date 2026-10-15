using Stride.Core.Serialization.Contents;
using Stride.Graphics;

namespace Stride.Editor.ImGui;

/// <summary>One real sprite loading cache and its mounted bundle lease, including failed loads.</summary>
internal sealed class SpriteSheetCacheResources : IDisposable, IRuntimeTextureConsumer
{
    internal ContentManager Cache { get; }
    private IDisposable? lease;
    private bool cacheReleased;
    internal bool IsDisposed => cacheReleased && lease == null;
    internal SpriteSheetCacheResources(CompiledProjectContent source)
    {
        lease = source.AcquireLease();
        try { Cache = source.CreateObjectCache(); }
        catch { lease.Dispose(); lease = null; throw; }
    }
    public bool UsesRuntimeTexture(Texture texture) => !IsDisposed && CompiledTextureConsumers.UsesTexture(Cache, texture);
    public void Dispose()
    {
        if (!cacheReleased) { Cache.UnloadAll(); cacheReleased = true; }
        if (lease != null) { lease.Dispose(); lease = null; }
    }
}
