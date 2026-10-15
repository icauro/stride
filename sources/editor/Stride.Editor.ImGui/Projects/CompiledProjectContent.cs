using Stride.Core.IO;
using Stride.Core;
using Stride.Core.Serialization.Contents;
using Stride.Core.Storage;
using Stride.Engine;

namespace Stride.Editor.ImGui;

/// <summary>One project bundle mount, shared by scene and asset-preview object caches.</summary>
internal sealed class CompiledProjectContent : IDisposable
{
    private readonly Game game;
    public string OutputPath { get; }
    private readonly FileSystemProvider mount;
    private readonly ObjectDatabase database;
    private readonly string bundleName;
    private readonly Action? releaseOutput;
    private bool bundleUnloaded, mountDisposed, outputReleased;
    private readonly object lifetime = new();
    private int leases;
    private bool disposalRequested;
    public CompiledProjectContent(Game game, string output, Action? releaseOutput = null)
    {
        this.game = game;
        OutputPath = Path.GetFullPath(output);
        this.releaseOutput = releaseOutput;
        var url = "/imgui-content-" + Guid.NewGuid().ToString("N");
        mount = new FileSystemProvider(url, output);
        database = game.Content.FileProvider.ObjectDatabase;
        bundleName = "imgui-project-" + Guid.NewGuid().ToString("N");
        try { database.BundleBackend.LoadBundleFromUrl(bundleName, database.ContentIndexMap, url + "/db/bundles/default.bundle").GetAwaiter().GetResult(); }
        catch { mount.Dispose(); throw; }
    }
    public ContentManager CreateObjectCache()
    {
        lock (lifetime) { ObjectDisposedException.ThrowIf(disposalRequested, this); return new(game.Services); }
    }
    internal ContentManager CreateObjectCache(IServiceRegistry services)
    {
        lock (lifetime) { ObjectDisposedException.ThrowIf(disposalRequested, this); return new(services); }
    }
    public IDisposable AcquireLease()
    {
        lock (lifetime)
        {
            ObjectDisposedException.ThrowIf(disposalRequested, this);
            var lease = new ContentLease(this); leases++; return lease;
        }
    }
    internal IDisposable DuplicateRetainedLease(IDisposable retained)
    {
        lock (lifetime)
        {
            ValidateRetainedLease(retained);
            var lease = new ContentLease(this); leases++; return lease;
        }
    }
    internal ContentManager CreateObjectCacheForRetainedLease(IDisposable retained)
    {
        lock (lifetime) { ValidateRetainedLease(retained); return new(game.Services); }
    }
    private void ValidateRetainedLease(IDisposable retained)
    {
        if (retained is not ContentLease lease || !ReferenceEquals(lease.Owner, this)
            || leases <= 0 || bundleUnloaded || mountDisposed || outputReleased)
            throw new ObjectDisposedException(nameof(retained), "Recovery requires an actual live lease of this exact compiled bundle.");
    }
    public void Dispose()
    {
        lock (lifetime) { disposalRequested = true; if (leases == 0) ReleasePhysicalContent(); }
    }
    private void ReleaseLease(ContentLease lease)
    {
        lock (lifetime)
        {
            if (!ReferenceEquals(lease.Owner, this)) return;
            if (leases <= 0) throw new InvalidOperationException("The compiled content lease has already been released.");
            // Retain the final real lease if a physical phase fails, so its
            // owner can retry without decrementing another participant.
            if (leases == 1 && disposalRequested) ReleasePhysicalContent();
            leases--;
            Volatile.Write(ref lease.Owner, null);
        }
    }
    private sealed class ContentLease(CompiledProjectContent content) : IDisposable
    {
        internal CompiledProjectContent? Owner = content;
        public void Dispose() => Volatile.Read(ref Owner)?.ReleaseLease(this);
    }
    private void ReleasePhysicalContent()
    {
        if (!bundleUnloaded)
        { database.BundleBackend.UnloadBundle(bundleName, database.ContentIndexMap); bundleUnloaded = true; }
        if (!mountDisposed) { mount.Dispose(); mountDisposed = true; }
        if (!outputReleased) { releaseOutput?.Invoke(); outputReleased = true; }
    }
}
