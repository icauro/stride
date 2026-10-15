using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Graphics;

namespace Stride.Editor.ImGui;

/// <summary>A detached real scene generation, including its native cache and canonical leases.</summary>
internal sealed class SceneViewportResources(Game game, CompiledProjectContent? source, IDisposable? mountLease) : IDisposable, IEditorResourceRetirementDrain, IRuntimeTextureConsumer
{
    private readonly EditorGame owner = game as EditorGame ?? throw new NotSupportedException("Scene retirement requires the editor ownership actor.");
    private readonly GraphicsDevice device = game.GraphicsDevice;
    private readonly List<IDisposable> resources = [], leases = [];
    private readonly List<IDisposable> afterCache = [];
    private readonly HashSet<IDisposable> retained = new(ReferenceEqualityComparer.Instance);
    private readonly List<(Task Workers, Func<bool> Pump)> drains = [];
    public bool IsRetirementDrainComplete => drains.Count == 0;
    public bool PumpRetirementDrain()
    {
        foreach (var drain in drains)
            if (!drain.Workers.IsCompleted || !drain.Pump()) return false;
        drains.Clear(); return true;
    }
    private ContentManager? cache;
    private readonly List<object> textureConsumerRoots = [];
    internal void RetainTextureConsumer(object? value) { if (value != null) textureConsumerRoots.Add(value); }
    public bool UsesRuntimeTexture(Texture texture)
    {
        if (IsDisposed) return false;
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return CompiledTextureConsumers.UsesTexture(cache, texture, visited)
            || textureConsumerRoots.Any(value => CompiledTextureConsumers.UsesTexture(value, texture, visited));
    }
    internal bool IsDisposed { get; private set; }
    private IDisposable? contentLease = mountLease;
    internal CompiledProjectContent? SourceContent { get; } = source;
    internal void Retain(IDisposable resource) { if (retained.Add(resource)) resources.Add(resource); }
    internal void RetainLease(IDisposable lease) { if (retained.Add(lease)) leases.Add(lease); }
    internal void RetainAfterCache(IDisposable resource) { if (retained.Add(resource)) afterCache.Add(resource); }
    internal void RetainCache(ContentManager? content) => cache = content;
    internal void RetainDrain(Task workers, Func<bool> pump) => drains.Add((workers, pump));
    internal void Retire() => owner.RetirePreviewTargets(this, device);
    public void Dispose()
    {
        if (!IsRetirementDrainComplete) throw new PreviewResourceDrainPendingException();
        while (resources.Count != 0) { resources[0].Dispose(); resources.RemoveAt(0); }
        if (cache != null) { cache.UnloadAll(); cache = null; }
        while (afterCache.Count != 0) { afterCache[0].Dispose(); afterCache.RemoveAt(0); }
        while (leases.Count != 0) { leases[0].Dispose(); leases.RemoveAt(0); }
        if (contentLease != null) { contentLease.Dispose(); contentLease = null; }
        retained.Clear();
        textureConsumerRoots.Clear();
        IsDisposed = true;
    }
}
