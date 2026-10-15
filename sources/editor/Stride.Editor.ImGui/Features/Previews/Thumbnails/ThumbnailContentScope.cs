using Stride.Core;
using Stride.Core.IO;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Graphics;
using Stride.Graphics.Font;

namespace Stride.Editor.ImGui;

/// <summary>Owns an entity thumbnail's content and actual font face/glyph caches.</summary>
internal sealed class ThumbnailContentScope : IDisposable
{
    private sealed class FontServices(IServiceRegistry parent, FontSystem fonts) : IServiceRegistry
    {
        private readonly ServiceRegistry local = Create(fonts);
        private static ServiceRegistry Create(FontSystem fonts)
        { var services = new ServiceRegistry(); services.AddService(fonts); return services; }
        public event EventHandler<ServiceEventArgs> ServiceAdded { add => local.ServiceAdded += value; remove => local.ServiceAdded -= value; }
        public event EventHandler<ServiceEventArgs> ServiceRemoved { add => local.ServiceRemoved += value; remove => local.ServiceRemoved -= value; }
        public void AddService<T>(T service) where T : class => local.AddService(service);
        public T? GetService<T>() where T : class => local.GetService<T>() ?? parent.GetService<T>();
        public void RemoveService<T>() where T : class => local.RemoveService<T>();
        public bool RemoveService<T>(T service) where T : class => local.RemoveService(service);
        public T GetOrCreate<T>() where T : class, IService
        { var value = GetService<T>(); if (value != null) return value; value = (T)T.NewInstance(this); AddService(value); return value; }
    }

    private readonly FontSystem fonts = new();
    private ContentManager? content;
    private IDisposable? contentLease;
    private readonly List<object> roots = [];
    private bool disposed;
    private bool fontsReleased;
    public ThumbnailContentScope(Game game, CompiledProjectContent source)
    {
        var owner = game as EditorGame ?? throw new NotSupportedException("Thumbnail font resources require the editor actor.");
        var device = game.GraphicsDevice;
        contentLease = source.AcquireLease();
        try
        {
            fonts.Load(device, game.Services.GetSafeServiceAs<IDatabaseFileProviderService>());
            content = source.CreateObjectCache(new FontServices(game.Services, fonts));
        }
        catch { owner.RetirePreviewTargets(this, device); throw; }
    }
    public T Load<T>(string url) where T : class
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var value = (content ?? throw new InvalidOperationException("Thumbnail content initialization did not complete."))
            .Load<T>(url, ContentManagerLoaderSettings.StreamingDisabled);
        roots.Add(value); return value;
    }
    public void FlushGlyphs() => fonts.Draw();
    public void Dispose()
    {
        if (disposed) return;
        if (!fontsReleased && !fonts.TryStopBitmapWorker()) throw new PreviewResourceDrainPendingException();
        if (content != null) { content.UnloadAll(); content = null; roots.Clear(); }
        if (!fontsReleased)
        {
            if (!fonts.TryUnload()) throw new PreviewResourceDrainPendingException();
            fontsReleased = true;
        }
        if (contentLease != null) { contentLease.Dispose(); contentLease = null; }
        disposed = true;
    }
}
