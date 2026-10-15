using Stride.Core;
using Stride.Core.IO;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Graphics;
using Stride.Graphics.Font;

namespace Stride.Editor.ImGui;

/// <summary>One actual native font factory/cache generation with its physical content lease.</summary>
internal sealed class CompiledFontResources : IDisposable, IMaterialPreviewContentOwner, IRuntimeTextureConsumer, IEditorResourceRetirementDrain
{
    private readonly EditorGame owner;
    private readonly Game game;
    private readonly FontSystem fonts = new();
    private ContentManager? cache;
    private IDisposable? contentLease;
    private bool fontsReleased;
    private bool bitmapWorkerStopped;
    public bool IsRetirementDrainComplete => bitmapWorkerStopped || IsDisposed;
    public bool PumpRetirementDrain()
    {
        if (IsRetirementDrainComplete) return true;
        bitmapWorkerStopped = fonts.TryStopBitmapWorker();
        return bitmapWorkerStopped;
    }
    internal GraphicsDevice Device { get; }
    internal long Generation { get; }
    internal bool IsDisposed { get; private set; }
    internal SpriteFont? Font { get; private set; }
    public CompiledProjectContent SourceContent { get; }
    internal CompiledFontResources(Game game, CompiledProjectContent bundle)
    {
        this.game = game; owner = game as EditorGame ?? throw new NotSupportedException("Font ownership requires the editor actor.");
        Device = game.GraphicsDevice; Generation = owner.RegisterFontResourceGeneration(Device);
        SourceContent = bundle; contentLease = bundle.AcquireLease();
    }
    internal void Initialize(string url)
    {
        fonts.Load(Device, game.Services.GetSafeServiceAs<IDatabaseFileProviderService>());
        cache = SourceContent.CreateObjectCache(new CompiledFontContent.FontServices(game.Services, fonts));
        Font = cache.Load<SpriteFont>(url, ContentManagerLoaderSettings.StreamingDisabled);
    }
    public bool UsesRuntimeTexture(Texture texture) => !IsDisposed
        && (CompiledTextureConsumers.UsesTexture(Font, texture) || CompiledTextureConsumers.UsesTexture(cache, texture));
    internal void Draw()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (!ReferenceEquals(Device, game.GraphicsDevice)) throw new InvalidOperationException("The font generation device changed before drawing.");
        fonts.Draw();
    }
    internal void Retire() => owner.RetirePreviewTargets(this, Device);
    internal bool ReleaseForGraphicsRecovery() => owner.ReleaseFontGenerationForRecovery(this);
    public void Dispose()
    {
        if (IsDisposed) return;
        if (!fontsReleased && !PumpRetirementDrain()) throw new PreviewResourceDrainPendingException();
        if (cache != null) { cache.UnloadAll(); cache = null; }
        if (!fontsReleased)
        {
            if (!fonts.TryUnload()) throw new PreviewResourceDrainPendingException();
            fontsReleased = true;
        }
        Font = null;
        if (contentLease != null) { contentLease.Dispose(); contentLease = null; }
        IsDisposed = true;
    }
}

internal sealed partial class EditorGame
{
    private long fontResourceGenerationSequence, certifiedFontResourceGeneration;
    internal long RegisterFontResourceGeneration(GraphicsDevice device)
    {
        if (!ReferenceEquals(device, GraphicsDevice)) throw new InvalidOperationException("Font initialization belongs to another device.");
        retiredEditorPreviewRevision++;
        return ++fontResourceGenerationSequence;
    }
    private void CertifyFontResourceGenerations() => certifiedFontResourceGeneration = fontResourceGenerationSequence;
    internal bool ReleaseFontGenerationForRecovery(CompiledFontResources resources)
    {
        if (resources.IsDisposed) return true;
        if (!IsEditorGraphicsRecovering || editorGpuRetirement != null || !editorGpuRetirementCertified
            || !ReferenceEquals(editorGpuRetirementCertifiedDevice, resources.Device)
            || resources.Generation > certifiedFontResourceGeneration)
        { resources.Retire(); return false; }
        EnsureEditorGpuRetirementCompleted();
        try { resources.Dispose(); return true; }
        catch (PreviewResourceDrainPendingException) { return false; }
    }
}
