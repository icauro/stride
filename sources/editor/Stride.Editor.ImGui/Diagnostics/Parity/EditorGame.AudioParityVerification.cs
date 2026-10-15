using Stride.Audio;
using Stride.Core;
using Stride.Core.IO;
using Stride.Core.Serialization.Contents;
using Stride.Media;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private AudioParityVerification? audioParity;
    internal bool StepAudioParityVerification(string compiledSoundUrl)
    {
        if (!Environment.GetCommandLineArgs().Contains("--ui-test")) throw new InvalidOperationException("Audio parity requires a private verification launch.");
        return (audioParity ??= new(this, projectContent ?? throw new InvalidOperationException("A real compiled sound bundle is required."), compiledSoundUrl)).Step();
    }
    internal object? AudioParityEvidence => audioParity?.Evidence;
    internal void CancelAudioParityVerification() { audioParity?.Dispose(); audioParity = null; }

    private sealed class AudioParityVerification : IAudioEngineProvider, IDisposable
    {
        public AudioEngine AudioEngine { get; }
        private readonly IDisposable lease;
        private readonly SoundAssetPreview preview;
        private readonly SoundInstance identity;
        private readonly long originalGeneration;
        private readonly TimeSpan range;
        private int phase;
        private bool disposed;
        internal object? Evidence { get; private set; }
        internal AudioParityVerification(EditorGame host, CompiledProjectContent bundle, string url)
        {
            lease = bundle.AcquireLease();
            AudioEngine? created = null;
            SoundAssetPreview? prepared = null;
            try
            {
                created = AudioEngineFactory.NewAudioEngine(); AudioEngine = created;
                if (created.State != AudioEngineState.Running) throw new InvalidOperationException("Private native audio device unavailable; A21 was not run.");
                var services = new ServiceRegistry();
                services.AddService<IAudioEngineProvider>(this);
                services.AddService<IDatabaseFileProviderService>(host.baseServicesDatabase());
                preview = prepared = new(created, bundle.CreateObjectCache(services), url);
                identity = preview.ParityInstance; originalGeneration = created.DeviceGeneration;
                preview.Volume = 0; preview.Loop = true;
                if (preview.Duration.TotalSeconds <= .1) throw new InvalidOperationException("Audio recovery requires an actual sound longer than 100 ms.");
                preview.Seek(Math.Min(.05, preview.Duration.TotalSeconds / 4)); range = preview.ParityRangeStart;
                preview.Play(); preview.Pause(); preview.Pump(); Require(preview);
            }
            catch { try { prepared?.Dispose(); } finally { try { created?.Dispose(); } finally { lease.Dispose(); } } throw; }
        }
        internal bool Step()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            AudioEngine.Update();
            if (phase == 0)
            {
                if (!AudioEngine.TryRecreateDevice()) return false;
                preview.Pump(); Require(preview);
                if (identity.PlayState != PlayState.Paused) throw new InvalidOperationException("Native recovery lost paused playback state.");
                preview.Play(); phase = 1; return false;
            }
            if (phase == 1)
            {
                if (!AudioEngine.TryRecreateDevice()) return false;
                preview.Pump(); Require(preview);
                if (!ReferenceEquals(identity, preview.ParityInstance) || identity.PlayState != PlayState.Playing
                    || preview.Volume != 0 || !preview.Loop || preview.ParityRangeStart != range
                    || AudioEngine.DeviceGeneration != originalGeneration + 2)
                    throw new InvalidOperationException("Native audio recovery lost instance, playback, range, loop or volume ownership.");
                Evidence = new { soundUrl = preview.SourceUrl, actualGeneration = AudioEngine.DeviceGeneration,
                    pausedRecovered = true, playingRecovered = true, sameInstance = true, volume = preview.Volume,
                    loop = preview.Loop, rangeStartSeconds = range.TotalSeconds, nativePositionSeconds = preview.Position.TotalSeconds };
                phase = 2;
            }
            Dispose(); return true;
        }
        private static void Require(SoundAssetPreview preview)
        { if (preview.PlaybackError != null) throw new InvalidOperationException("Private native audio fixture failed.", preview.PlaybackError); }
        public void Dispose()
        {
            if (disposed) return;
            preview.Dispose(); AudioEngine.Dispose(); lease.Dispose(); disposed = true;
        }
    }
    private IDatabaseFileProviderService baseServicesDatabase() => base.Services.GetService<IDatabaseFileProviderService>()
        ?? throw new InvalidOperationException("The actual project database provider is unavailable.");
}
