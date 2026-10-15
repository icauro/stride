using Stride.Core.Serialization.Contents;
using Stride.Graphics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class SceneTextureContentOwner(CompiledProjectContent content, IDisposable lease)
    {
        internal readonly CompiledProjectContent Content = content;
        internal readonly IDisposable Lease = lease;
        internal bool CacheReleased;
    }

    private readonly Dictionary<Texture, SceneTextureContentOwner> sceneTextureContentOwners = new(ReferenceEqualityComparer.Instance);

    private void RetainSceneTextureContent(Texture texture, CompiledProjectContent content)
    {
        var lease = content.AcquireLease();
        try { sceneTextureContentOwners.Add(texture, new(content, lease)); }
        catch { lease.Dispose(); throw; }
    }

    private void ReleaseSceneTextureContent(ContentManager cache, Texture texture)
    {
        if (TextureHasConsumers(texture)) throw new PreviewResourceDrainPendingException();
        if (!sceneTextureContentOwners.TryGetValue(texture, out var owner))
        {
            cache.Unload(texture);
            return;
        }
        if (!owner.CacheReleased) { cache.Unload(texture); owner.CacheReleased = true; }
        owner.Lease.Dispose();
        sceneTextureContentOwners.Remove(texture);
    }
}
