using Stride.Core.Serialization.Contents;
using Stride.Graphics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class FailedRecoveryTextureCache(ContentManager cache, IDisposable pin) : IDisposable
    {
        private bool cacheReleased;
        public void Dispose()
        {
            if (!cacheReleased) { cache.UnloadAll(); cacheReleased = true; }
            pin.Dispose();
        }
    }
    private void ReleaseSceneTextureGraphicsForRecovery(ContentManager cache, Texture texture)
    {
        if (!sceneTextureContentOwners.TryGetValue(texture, out var owner))
            throw new InvalidOperationException("The actual scene texture content owner is unavailable for recovery.");
        if (!owner.CacheReleased) { cache.Unload(texture); owner.CacheReleased = true; }
    }
    private (ContentManager Cache, Texture Texture) RebindSceneTextureGraphicsAfterRecovery(string url, Texture previous)
    {
        if (!sceneTextureContentOwners.TryGetValue(previous, out var owner) || !owner.CacheReleased)
            throw new InvalidOperationException("The actual scene texture owner must finish old-device cache release before rebind.");
        var pin = owner.Content.DuplicateRetainedLease(owner.Lease);
        ContentManager? cache = null;
        try
        {
            cache = owner.Content.CreateObjectCacheForRetainedLease(pin);
            var texture = cache.Load<Texture>(url, ContentManagerLoaderSettings.StreamingDisabled);
            sceneTextureContentOwners.Add(texture, owner);
            sceneTextureContentOwners.Remove(previous);
            owner.CacheReleased = false;
            pin.Dispose();
            return (cache, texture);
        }
        catch
        {
            if (cache != null) RetireEditorPreview(new FailedRecoveryTextureCache(cache, pin));
            else pin.Dispose();
            throw;
        }
    }
}
