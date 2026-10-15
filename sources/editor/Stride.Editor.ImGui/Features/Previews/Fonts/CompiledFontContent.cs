using Stride.Core;
using Stride.Core.IO;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Graphics;
using Stride.Graphics.Font;

namespace Stride.Editor.ImGui;

/// <summary>Owns actual engine glyph/face caches for one compiled font generation.</summary>
internal sealed class CompiledFontContent : IDisposable, IRuntimeTextureConsumer
{
    internal sealed class FontServices(IServiceRegistry parent, FontSystem fonts) : IServiceRegistry
    {
        private readonly ServiceRegistry local = Create(fonts);
        private static ServiceRegistry Create(FontSystem fonts) { var services = new ServiceRegistry(); services.AddService(fonts); return services; }
        public event EventHandler<ServiceEventArgs> ServiceAdded { add => local.ServiceAdded += value; remove => local.ServiceAdded -= value; }
        public event EventHandler<ServiceEventArgs> ServiceRemoved { add => local.ServiceRemoved += value; remove => local.ServiceRemoved -= value; }
        public void AddService<T>(T service) where T : class => local.AddService(service);
        public T? GetService<T>() where T : class => local.GetService<T>() ?? parent.GetService<T>();
        public void RemoveService<T>() where T : class => local.RemoveService<T>();
        public bool RemoveService<T>(T serviceObject) where T : class => local.RemoveService(serviceObject);
        public T GetOrCreate<T>() where T : class, IService
        { var value = GetService<T>(); if (value != null) return value; value = (T)T.NewInstance(this); AddService(value); return value; }
    }
    private CompiledFontResources? resources;
    private readonly Game game;
    private readonly string url;
    private readonly CompiledProjectContent? ownedBundle;
    private bool disposed;
    public SpriteFont Font => resources?.Font ?? throw new InvalidOperationException("The font generation has no active graphics resources.");
    internal SpriteFont? ActiveFont => resources?.Font;
    internal bool HasGraphicsResources => resources != null;
    internal bool IsRetirementDrainComplete => resources?.IsRetirementDrainComplete != false;
    internal bool PumpRetirementDrain() => resources?.PumpRetirementDrain() != false;
    public CompiledProjectContent Bundle { get; }
    public CompiledFontContent(Game game, CompiledProjectContent bundle, string url)
        : this(game, bundle, url, false) { }
    private CompiledFontContent(Game game, CompiledProjectContent bundle, string url, bool ownBundle)
    {
        this.game = game; this.url = url;
        Bundle = bundle; ownedBundle = ownBundle ? bundle : null;
        CompiledFontResources? candidate = null;
        try
        {
            candidate = new CompiledFontResources(game, bundle); candidate.Initialize(url);
            resources = candidate;
        }
        catch { candidate?.Retire(); ownedBundle?.Dispose(); throw; }
    }
    internal static CompiledFontContent AdoptCompiledOutput(Game game, ref string? output, string url)
    {
        var ownedOutput = output ?? throw new ArgumentNullException(nameof(output));
        var bundle = new CompiledProjectContent(game, ownedOutput, () => CompiledFontBuild.ReleaseOutput(ownedOutput));
        output = null; // The real returned mount now owns this compiler output.
        return new CompiledFontContent(game, bundle, url, true);
    }
    public bool UsesRuntimeTexture(Texture texture) => !disposed && resources?.UsesRuntimeTexture(texture) == true;
    public void Draw() { ObjectDisposedException.ThrowIf(disposed, this); resources?.Draw(); }
    internal void RetireGraphicsResources()
    {
        if (disposed || resources == null) return;
        resources.Retire();
        resources = null; // The retirement actor owns the exact old generation; the compiled source remains live.
    }
    public void ReleaseGraphicsResourcesForRecovery()
    {
        if (disposed) throw new ObjectDisposedException(nameof(CompiledFontContent));
        if (resources == null) return;
        if (!resources.ReleaseForGraphicsRecovery())
            throw new InvalidOperationException("This font generation is waiting for its actual GPU retirement certificate.");
        resources = null;
    }
    public void RebindGraphicsResourcesAfterRecovery()
    {
        if (disposed) throw new ObjectDisposedException(nameof(CompiledFontContent));
        if (resources != null) return;
        var candidate = new CompiledFontResources(game, Bundle);
        try { candidate.Initialize(url); resources = candidate; }
        catch { candidate.Retire(); throw; }
    }
    public void Dispose()
    {
        if (disposed) return;
        if (resources != null) { resources.Retire(); resources = null; }
        ownedBundle?.Dispose(); disposed = true;
    }
}
