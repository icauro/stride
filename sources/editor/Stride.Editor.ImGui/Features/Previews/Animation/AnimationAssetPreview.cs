using Hexa.NET.ImGui;
using Stride.Animations;
using Stride.Core.Mathematics;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Rendering;

namespace Stride.Editor.ImGui;

/// <summary>Actual imported model, skeleton and engine animation processor in an isolated preview scene.</summary>
internal sealed class AnimationAssetPreview : IDisposable, IRuntimeTextureConsumer
{
    private readonly ContentManager cache;
    private readonly Model model;
    private readonly AnimationClip clip;
    private readonly ModelAssetPreview view;
    private readonly PlayingAnimation playing;
    private CompiledProjectContent? ownedBundle;
    internal string CaptureSourceUrl { get; }
    internal bool CaptureReady => !disposed && view.Visible && view.RenderError == null && view.RenderCount > 0 && view.TargetWidth > 0;
    public CompiledProjectContent SourceContent { get; }
    public CompiledProjectContent? DetachOwnedContentForRecovery()
    { var value = ownedBundle; ownedBundle = null; return value; }
    private bool disposed, scrubbing, resumeAfterScrub;
    public bool UsesRuntimeTexture(Texture texture) => !disposed && (view?.UsesRuntimeTexture(texture) == true
        || CompiledTextureConsumers.UsesTexture(model, texture) || CompiledTextureConsumers.UsesTexture(cache, texture));
    public bool Visible { get => view.Visible; set => view.Visible = value; }
    public ImTextureRef TextureRef => view.TextureRef;
    public AssetPreviewCamera Camera => view.Camera;
    public Exception? RenderError => view.RenderError;
    public double CurrentSeconds => playing.CurrentTime.TotalSeconds;
    public double DurationSeconds => clip.Duration.TotalSeconds;
    public bool IsPlaying => playing.Enabled;
    public float TimeScale
    {
        get => playing.TimeFactor;
        set
        {
            if (!float.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            playing.TimeFactor = value;
        }
    }
    public bool Loop
    {
        get => playing.RepeatMode == AnimationRepeatMode.LoopInfinite;
        set => playing.RepeatMode = value ? AnimationRepeatMode.LoopInfinite : AnimationRepeatMode.PlayOnceHold;
    }
    public AnimationAssetPreview(Game game, ImGuiRenderer renderer, CompiledProjectContent content,
        string modelUrl, string clipUrl, bool hdr, ColorSpace colorSpace, bool ownsContent = false)
    {
        CaptureSourceUrl = clipUrl; SourceContent = content;
        cache = content.CreateObjectCache();
        Model? loadedModel = null; AnimationClip? loadedClip = null; ModelAssetPreview? created = null;
        try
        {
            loadedModel = cache.Load<Model>(modelUrl, ContentManagerLoaderSettings.StreamingDisabled);
            if (loadedModel.Skeleton == null) throw new InvalidDataException("The animation preview model has no compiled skeleton.");
            loadedClip = cache.Load<AnimationClip>(clipUrl, ContentManagerLoaderSettings.StreamingDisabled);
            if (loadedClip.Duration < TimeSpan.Zero) throw new InvalidDataException("The animation clip has an invalid duration.");
            created = new ModelAssetPreview(game, renderer, loadedModel, modelUrl, hdr,
                "StrideEditorForwardShadingEffect", new Vector2(MathUtil.PiOverFour, -MathUtil.Pi / 6), colorSpace);
            playing = created.AttachPreviewAnimation(loadedClip);
            model = loadedModel; clip = loadedClip; view = created;
            ownedBundle = ownsContent ? content : null;
        }
        catch
        {
            created?.Dispose();
            if (loadedClip != null) cache.Unload(loadedClip);
            if (loadedModel != null) cache.Unload(loadedModel);
            throw;
        }
    }
    public void Play() { ThrowIfDisposed(); playing.Enabled = true; }
    public void Pause() { ThrowIfDisposed(); playing.Enabled = false; }
    public void Stop() { Pause(); Seek(0); }
    public void Seek(double seconds)
    {
        ThrowIfDisposed();
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        // The engine applies modulo to looping clips. Retain the last visible
        // sample at the end instead of wrapping the scrubber to the first pose.
        var maximum = Math.Max(0, clip.Duration.TotalSeconds - .001);
        playing.CurrentTime = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, maximum));
    }
    public void BeginScrub()
    {
        if (scrubbing) return;
        scrubbing = true; resumeAfterScrub = IsPlaying; Pause();
    }
    public void EndScrub()
    {
        if (!scrubbing) return;
        scrubbing = false;
        if (resumeAfterScrub) Play();
    }
    public void ResetCamera() => view.ResetCamera();
    public void Resize(int width, int height) => view.Resize(width, height);
    public void Render(GameTime time)
    {
        if (disposed) return;
        view.Render(time);
        if (!Loop && playing.CurrentTime >= clip.Duration) playing.Enabled = false;
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    public void Dispose()
    {
        if (disposed) return; playing.Enabled = false;
        scrubbing = resumeAfterScrub = false;
        if (!viewReleased) { view.Dispose(); viewReleased = true; }
        if (!clipReleased) { cache.Unload(clip); clipReleased = true; }
        if (!modelReleased) { cache.Unload(model); modelReleased = true; }
        ownedBundle?.Dispose(); ownedBundle = null; disposed = true;
    }
    private bool viewReleased, clipReleased, modelReleased;
}
