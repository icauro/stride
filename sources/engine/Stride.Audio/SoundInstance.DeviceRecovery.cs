using System;
using Stride.Media;

namespace Stride.Audio
{
    public partial class SoundInstance
    {
        internal AudioEngine DeviceEngine => engine;
        internal Sound DeviceSound => sound as Sound;
        private int deviceSourceSampleRate;
        private bool deviceSourceMono, deviceSourceStreamed, deviceSourceHrtf;
        private float deviceSourceDirectionalFactor;
        private HrtfEnvironment deviceSourceEnvironment;
        private PlayRange devicePlayRange;
        private TimeSpan retainedDevicePosition;
        private AudioEmitter deviceEmitter;
        internal void ConfigureDeviceSource(int sampleRate, bool mono, bool streamed, bool hrtf, float direction, HrtfEnvironment environment)
        {
            deviceSourceSampleRate = sampleRate; deviceSourceMono = mono; deviceSourceStreamed = streamed;
            deviceSourceHrtf = hrtf; deviceSourceDirectionalFactor = direction; deviceSourceEnvironment = environment;
        }
        internal bool UsesDynamicDeviceSource => soundSource != null;
        internal bool CanRecreateDeviceSource => Source.Ptr != IntPtr.Zero && deviceSourceSampleRate > 0 && (soundSource == null ? sound is Sound : soundSource.CanPreserveDeviceBuffers);
        internal sealed class DeviceSourceReplacement
        {
            internal SoundInstance Owner;
            internal AudioLayer.Source Original, Replacement;
            internal PlayState Playback;
            internal TimeSpan Position;
            internal int NativePlayback;
            internal DynamicSoundSource.DeviceBufferReplacement Buffers;
        }
        internal DeviceSourceReplacement CaptureDevicePlayback()
        {
            var playback = engine.State == AudioEngineState.Invalidated ? playState : PlayState;
            var position = playback == PlayState.Playing && engine.State != AudioEngineState.Invalidated ? Position : retainedDevicePosition;
            return new() { Owner = this, Original = Source, Playback = playback, Position = position,
                NativePlayback = soundSource != null ? AudioLayer.GetRecoveryPlaybackState(Source)
                    : playback == PlayState.Playing ? 1 : playback == PlayState.Paused ? 2 : 0 };
        }
        internal DeviceSourceReplacement PrepareDeviceSource(AudioLayer.Listener listener, AudioLayer.Buffer buffer, DeviceSourceReplacement snapshot)
        {
            if (!CanRecreateDeviceSource) throw new NotSupportedException("This active dynamic sound source has no native device recreation adapter.");
            var replacement = AudioLayer.SourceCreate(listener, deviceSourceSampleRate,
                soundSource?.MaxNumberOfBuffers ?? 1, deviceSourceMono, spatialized, soundSource != null,
                deviceSourceHrtf, deviceSourceDirectionalFactor, deviceSourceEnvironment);
            if (replacement.Ptr == IntPtr.Zero) throw new InvalidOperationException("The replacement audio source could not be created.");
            // The engine tracks this stage before preparation, and owns release/retry
            // if any buffer allocation, native queue copy or property application fails.
            snapshot.Replacement = replacement;
            if (soundSource == null) AudioLayer.SourceSetBuffer(replacement, buffer);
            else
            {
                try { snapshot.Buffers = soundSource.PrepareDeviceBuffers(replacement); }
                catch (DynamicSoundSource.DeviceBufferPreparationException error)
                { snapshot.Buffers = new() { Replacement = error.Buffers }; throw; }
            }
            deviceEmitter?.Apply3D(replacement);
            AudioLayer.SourceSetGain(replacement, volume); AudioLayer.SourceSetPitch(replacement, pitch); AudioLayer.SourceSetPan(replacement, pan);
            if (soundSource == null)
            {
                AudioLayer.SourceSetLooping(replacement, isLooping);
                AudioLayer.SourceSetRange(replacement, devicePlayRange.Start.TotalSeconds, devicePlayRange.End.TotalSeconds);
                if (!AudioLayer.CanPreserveStaticPlaybackRange(replacement))
                    throw new NotSupportedException("The loaded audio backend cannot preserve this actual playback range.");
                if (snapshot.Position > TimeSpan.Zero && !AudioLayer.TrySetPlaybackPosition(replacement, snapshot.Position.TotalSeconds))
                    throw new NotSupportedException("The loaded audio backend cannot restore the actual playback position.");
            }
            return snapshot;
        }

        internal void PublishDeviceSource(DeviceSourceReplacement replacement)
        {
            if (replacement.Buffers != null) soundSource.PublishDeviceBuffers(replacement.Buffers);
            Source = replacement.Replacement; playState = replacement.Playback; retainedDevicePosition = replacement.Position;
            if (replacement.NativePlayback == 1) AudioLayer.SourcePlay(Source);
            else if (replacement.NativePlayback == 2) { AudioLayer.SourcePlay(Source); AudioLayer.SourcePause(Source); }
            else if (soundSource != null && replacement.Playback == PlayState.Playing) soundSource.ResumePendingDevicePlay();
        }
    }
}
