using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Graphics;

namespace Stride.Editor.ImGui;

/// <summary>Owns the actual compiled sheet, atlas GPU resources, and their bundle mount.</summary>
internal sealed class CompiledSpriteSheetContent : IDisposable
{
    private readonly CompiledProjectContent content;
    private readonly Action<IDisposable> retire;
    private SpriteSheetCacheResources? resources;
    private bool disposed;
    private readonly Func<CompiledSpriteSheetContent, bool>? hasConsumers;
    public SpriteSheet Sheet { get; private set; } = null!;
    public bool IsDisposed => disposed;
    public string Url { get; }
    public CompiledProjectContent Bundle => content;
    internal Texture[] SnapshotOwnedTextures()
    {
        var textures = new HashSet<Texture>(ReferenceEqualityComparer.Instance);
        if (resources != null && !resources.IsDisposed)
            foreach (var texture in resources.Cache.GetLoadedAssetsSnapshot().OfType<Texture>()) textures.Add(texture);
        if (Sheet != null) foreach (var sprite in Sheet.Sprites) if (sprite.Texture != null) textures.Add(sprite.Texture);
        return textures.ToArray();
    }

    public CompiledSpriteSheetContent(Game game, string output, string url, Func<CompiledSpriteSheetContent, bool>? hasConsumers = null)
    {
        var editor = game as EditorGame ?? throw new NotSupportedException("Sprite content retirement requires the editor ownership actor.");
        Url = url; this.hasConsumers = hasConsumers;
        retire = resource => editor.RetirePreviewTargets(resource, game.GraphicsDevice);
        content = new CompiledProjectContent(game, output, () => CompiledSpriteSheetBuild.ReleaseOutput(output));
        try
        {
            resources = new SpriteSheetCacheResources(content);
            Sheet = resources.Cache.Load<SpriteSheet>(url, ContentManagerLoaderSettings.StreamingDisabled);
        }
        catch { retire(this); throw; }
    }

    public void Dispose()
    {
        if (disposed) return;
        if (Sheet != null && hasConsumers?.Invoke(this) == true) throw new PreviewResourceDrainPendingException();
        resources?.Dispose();
        content.Dispose(); disposed = true;
    }

    public void ReleaseGraphicsResourcesForRecovery()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (resources?.IsDisposed != false) return;
        if (Sheet != null && hasConsumers?.Invoke(this) == true) throw new PreviewResourceDrainPendingException();
        resources.Dispose();
    }
    public void RebindGraphicsResourcesAfterRecovery()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (resources?.IsDisposed == false) return;
        var replacement = new SpriteSheetCacheResources(content);
        try
        {
            var sheet = replacement.Cache.Load<SpriteSheet>(Url, ContentManagerLoaderSettings.StreamingDisabled);
            resources = replacement; Sheet = sheet;
        }
        catch { retire(replacement); throw; }
    }
}
