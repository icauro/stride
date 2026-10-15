using Stride.Audio;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Media;

namespace Stride.Editor.ImGui;

/// <summary>One actual engine sound instance, with the same in-memory preview policy as WPF.</summary>
internal sealed class SoundAssetPreview : IDisposable
{
    private readonly ContentManager cache;
    private readonly Sound sound;
    private readonly SoundInstance instance;
    private readonly AudioEngine engine;
    private readonly CompiledProjectContent? ownedBundle;
    private bool disposed;
    private int releasePhase;
    private TimeSpan retainedPosition;
    private TimeSpan rangeStart;
    private long deviceGeneration;
    private bool devicePlaybackError;
    public Exception? PlaybackError { get; private set; }
    public TimeSpan Duration => sound.TotalLength;
    public TimeSpan Position { get; private set; }
    public bool IsPlaying { get; private set; }
    internal string SourceUrl { get; }
    public bool IsValid => !disposed && PlaybackError == null;
    public float Volume
    {
        get => instance.Volume;
        set => Invoke(() => instance.Volume = Math.Clamp(value, 0, 1));
    }
    public bool Loop
    {
        get => instance.IsLooping;
        set => Invoke(() => instance.IsLooping = value);
    }
    public SoundAssetPreview(Game game, CompiledProjectContent content, string url, bool ownsContent = false)
        : this(game.Services.GetService<IAudioEngineProvider>()?.AudioEngine
            ?? throw new InvalidOperationException("The engine audio device is unavailable."), content.CreateObjectCache(), url,
            ownsContent ? content : null) { }
    internal SoundAssetPreview(AudioEngine engine, ContentManager cache, string url, CompiledProjectContent? ownedBundle = null)
    {
        SourceUrl = url;
        this.engine = engine; this.cache = cache;
        CheckDevice(); deviceGeneration = engine.DeviceGeneration;
        Sound? loaded = null; SoundInstance? created = null;
        try
        {
            loaded = cache.Load<Sound>(url, ContentManagerLoaderSettings.StreamingDisabled);
            created = loaded.CreateInstance(null, true);
            created.SetRange(new PlayRange(TimeSpan.Zero, TimeSpan.Zero));
            sound = loaded; instance = created;
            this.ownedBundle = ownedBundle;
        }
        catch
        {
            created?.Dispose(); if (loaded != null) cache.Unload(loaded); throw;
        }
    }
    internal SoundInstance ParityInstance => instance;
    internal TimeSpan ParityRangeStart => rangeStart;
    private void CheckDevice()
    {
        if (engine.State is AudioEngineState.Invalidated or AudioEngineState.Disposed)
            throw new InvalidOperationException("The engine audio device is unavailable.");
    }
    public void Pump()
    {
        if (disposed) return;
        if (deviceGeneration != engine.DeviceGeneration && engine.State is AudioEngineState.Running or AudioEngineState.Paused)
        {
            deviceGeneration = engine.DeviceGeneration;
            if (devicePlaybackError) { PlaybackError = null; devicePlaybackError = false; }
        }
        if (PlaybackError != null) return;
        Invoke(() =>
        {
            var state = instance.PlayState;
            if (state == PlayState.Playing)
            {
                // Position comes from the native audio source; no UI clock or
                // synthesized elapsed-time counter participates in playback.
                Position = retainedPosition = instance.Position;
            }
            else if (state == PlayState.Paused) Position = retainedPosition;
            else if (IsPlaying) Position = retainedPosition = rangeStart;
            IsPlaying = state == PlayState.Playing;
        });
    }
    public void Play() => Invoke(() => { instance.Play(); IsPlaying = instance.PlayState == PlayState.Playing; });
    public void Pause() => Invoke(() =>
    {
        if (instance.PlayState == PlayState.Playing) retainedPosition = Position = instance.Position;
        instance.Pause(); IsPlaying = false;
    });
    public void Stop() => Invoke(() =>
    {
        instance.Stop(); instance.SetRange(new PlayRange(TimeSpan.Zero, TimeSpan.Zero));
        Position = retainedPosition = rangeStart = TimeSpan.Zero; IsPlaying = false;
    });
    public void Seek(double seconds) => Invoke(() =>
    {
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        var resume = instance.PlayState == PlayState.Playing;
        instance.Stop();
        var maximum = Math.Max(0, Duration.TotalSeconds - .001);
        var selected = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, maximum));
        instance.SetRange(new PlayRange(selected, Duration));
        rangeStart = retainedPosition = Position = selected;
        if (resume) instance.Play();
        IsPlaying = instance.PlayState == PlayState.Playing;
    });
    private void Invoke(Action action)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (PlaybackError != null) return;
        try { CheckDevice(); action(); }
        catch (Exception error)
        {
            devicePlaybackError = engine.State is AudioEngineState.Invalidated or AudioEngineState.Disposed;
            PlaybackError = error; IsPlaying = false;
            try { instance.Stop(); } catch { }
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        IsPlaying = false;
        if (releasePhase == 0) { if (!instance.IsDisposed) { instance.Stop(); instance.Dispose(); } releasePhase = 1; }
        if (releasePhase == 1) { cache.Unload(sound); releasePhase = 2; }
        if (releasePhase == 2) { ownedBundle?.Dispose(); releasePhase = 3; }
        disposed = true;
    }
}
