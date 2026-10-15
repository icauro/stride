// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
#if STRIDE_PLATFORM_IOS || STRIDE_PLATFORM_MACOS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AVFoundation;
using Foundation;
using Stride.Core.Mathematics;

namespace Stride.Audio
{
    /// <summary>
    /// AVAudioEngine-backed managed implementation of <see cref="AudioLayer"/>. Replaces
    /// libstrideaudio's OpenAL backend on Apple platforms. Handles flow through GCHandle: each
    /// public Device/Listener/Source/Buffer.Ptr wraps a GCHandle to a <see cref="ManagedDevice"/>,
    /// <see cref="ManagedListener"/>, etc., resolved with <see cref="ResolveHandle"/>.
    /// </summary>
    public partial class AudioLayer
    {
        // -- Handle plumbing -----------------------------------------------------

        private static IntPtr AllocHandle<T>(T obj) where T : class
        {
            return GCHandle.ToIntPtr(GCHandle.Alloc(obj, GCHandleType.Normal));
        }

        private static T ResolveHandle<T>(IntPtr ptr) where T : class
        {
            if (ptr == IntPtr.Zero) return null;
            var handle = GCHandle.FromIntPtr(ptr);
            return handle.Target as T;
        }

        private static void FreeHandle(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return;
            GCHandle.FromIntPtr(ptr).Free();
        }

        // -- Managed wrappers ----------------------------------------------------

        private sealed partial class ManagedDevice
        {
            public AVAudioEngine Engine;
            public AVAudioMixerNode Mixer;          // == Engine.MainMixerNode; cached for master volume
            public AVAudioEnvironmentNode Env;      // 3D mixer; spatialized sources connect here
            public List<ManagedSource> Sources = new();
            public bool EngineStopped, EnvironmentDetached, EnvironmentReleased, EngineReleased;
        }

        private sealed class ManagedListener
        {
            public ManagedDevice Device;
            public bool Enabled;
        }

        private sealed class ManagedBuffer
        {
            public IntPtr NativeHandle;
            public long ScheduleGeneration;
            public int CapacityBytes;               // max bytes BufferFill can write
            public AVAudioPcmBuffer Pcm;            // null until BufferFill is called the first time
            public AudioLayer.BufferType StreamType;
            // Owner's current schedule state — used by the streaming path to know when a buffer
            // becomes free again after a Source consumed it.
            public bool Scheduled;
        }

        private sealed partial class ManagedSource
        {
            public ManagedDevice Device;
            public ManagedListener Listener;
            public AVAudioPlayerNode Player;
            public AVAudioFormat Format;            // declared input format (int16 mono/stereo)
            public int SampleRate;
            public bool Mono;
            public bool Spatialized;
            public bool Streamed;
            public bool Looping;
            public float Gain = 1f;
            public bool StaticBufferScheduled;
            public ManagedBuffer StaticBuffer;      // SourceSetBuffer target; replays on Play
            public Queue<ManagedBuffer> StreamingFree = new();  // actual pool tokens returned after native playback
            public readonly object ScheduleGate = new();
            public readonly List<NativeAudioSchedule> NativeSchedules = new();
            public long ScheduledFrames;
            public bool ScheduleDisposed;
            public bool PlayerStopped, PlayerDetached, PlayerReleased, FormatReleased;
            public double PlayRangeStart, PlayRangeStop;
        }

        // -- Init / Device -------------------------------------------------------

        public static bool Init()
        {
            return true;
        }

        public static Device Create(string deviceName, DeviceFlags flags)
        {
            try
            {
                var dev = new ManagedDevice
                {
                    Engine = new AVAudioEngine(),
                };
                dev.Mixer = dev.Engine.MainMixerNode;
                // EnvironmentNode is set up lazily on first spatialized SourceCreate. AVAudioEngine
                // on macOS deadlocks during Start if AttachNode/Connect happen before MainMixerNode
                // has been touched and the output node format is fully resolved.
                if (!dev.Engine.StartAndReturnError(out var error) || error != null)
                {
                    AudioEngine.Logger?.Warning($"AVAudioEngine.Start failed: {error?.LocalizedDescription}");
                    return default;
                }
                return new Device { Ptr = AllocHandle(dev) };
            }
            catch (Exception ex)
            {
                AudioEngine.Logger?.Error($"AudioLayer.Create threw: {ex}");
                return default;
            }
        }

        private static void EnsureEnv(ManagedDevice dev)
        {
            if (dev.Env != null) return;
            dev.Env = new AVAudioEnvironmentNode();
            dev.Engine.AttachNode(dev.Env);
            dev.Engine.Connect(dev.Env, dev.Mixer, null);
        }

        public static void Destroy(Device device)
        {
            var dev = ResolveHandle<ManagedDevice>(device.Ptr);
            if (dev == null) return;

            foreach (var src in new List<ManagedSource>(dev.Sources)) DestroyNativeSourceResources(src);
            if (!dev.EngineStopped) { dev.Engine.Stop(); dev.EngineStopped = true; }
            if (!dev.EnvironmentDetached)
            {
                if (dev.Env != null) dev.Engine.DetachNode(dev.Env);
                dev.EnvironmentDetached = true;
            }
            if (!dev.EnvironmentReleased) { dev.Env?.Dispose(); dev.EnvironmentReleased = true; }
            if (!dev.EngineReleased) { dev.Engine.Dispose(); dev.EngineReleased = true; }
            FreeHandle(device.Ptr);
        }

        public static void Update(Device device)
        {
            // AVAudioEngine is push-driven; no per-frame work required.
        }

        public static void SetMasterVolume(Device device, float volume)
        {
            var dev = ResolveHandle<ManagedDevice>(device.Ptr);
            if (dev?.Mixer == null) return;
            dev.Mixer.OutputVolume = volume;
        }

        // -- Listener ------------------------------------------------------------

        public static Listener ListenerCreate(Device device)
        {
            var dev = ResolveHandle<ManagedDevice>(device.Ptr);
            if (dev == null) return default;
            var listener = new ManagedListener { Device = dev };
            return new Listener { Ptr = AllocHandle(listener) };
        }

        public static void ListenerDestroy(Listener listener) => FreeHandle(listener.Ptr);

        public static bool ListenerEnable(Listener listener)
        {
            var l = ResolveHandle<ManagedListener>(listener.Ptr);
            if (l == null) return false;
            l.Enabled = true;
            return true;
        }

        public static void ListenerDisable(Listener listener)
        {
            var l = ResolveHandle<ManagedListener>(listener.Ptr);
            if (l != null) l.Enabled = false;
        }

        public static void ListenerPush3D(Listener listener, ref Vector3 pos, ref Vector3 forward, ref Vector3 up, ref Vector3 vel, ref Matrix worldTransform)
        {
            var l = ResolveHandle<ManagedListener>(listener.Ptr);
            if (l?.Device?.Env == null) return;
            l.Device.Env.ListenerPosition = new System.Numerics.Vector3(pos.X, pos.Y, pos.Z);
            l.Device.Env.ListenerVectorOrientation = new AVAudio3DVectorOrientation(
                new System.Numerics.Vector3(forward.X, forward.Y, forward.Z),
                new System.Numerics.Vector3(up.X, up.Y, up.Z));
        }

        // -- Buffer --------------------------------------------------------------

        public static Buffer BufferCreate(int maxBufferSizeBytes)
        {
            var buf = new ManagedBuffer { CapacityBytes = maxBufferSizeBytes };
            buf.NativeHandle = AllocHandle(buf);
            return new Buffer { Ptr = buf.NativeHandle };
        }

        public static void BufferDestroy(Buffer buffer)
        {
            var b = ResolveHandle<ManagedBuffer>(buffer.Ptr);
            if (b != null)
            {
                b.Pcm?.Dispose();
                b.Pcm = null;
                b.NativeHandle = IntPtr.Zero;
            }
            FreeHandle(buffer.Ptr);
        }

        public static unsafe void BufferFill(Buffer buffer, IntPtr pcm, int bufferSize, int sampleRate, bool mono)
        {
            var b = ResolveHandle<ManagedBuffer>(buffer.Ptr);
            if (b == null || pcm == IntPtr.Zero || bufferSize <= 0) return;
            if (bufferSize > b.CapacityBytes || sampleRate <= 0 || bufferSize % (sizeof(short) * (mono ? 1 : 2)) != 0)
                throw new ArgumentOutOfRangeException(nameof(bufferSize), "The PCM packet must fit its real native pool buffer and contain complete frames.");

            uint channels = mono ? 1u : 2u;
            // Stride writes int16 interleaved PCM into the buffer.
            using var format = new AVAudioFormat(AVAudioCommonFormat.PCMInt16, sampleRate, channels, interleaved: true);
            uint frameCapacity = (uint)(bufferSize / (2 * channels));
            if (b.Pcm == null || b.Pcm.FrameCapacity < frameCapacity || b.Pcm.Format == null || (int)b.Pcm.Format.SampleRate != sampleRate || b.Pcm.Format.ChannelCount != channels)
            {
                b.Pcm?.Dispose();
                b.Pcm = new AVAudioPcmBuffer(format, frameCapacity);
            }
            // AVAudioPcmBuffer.Int16ChannelData is one pointer per channel; interleaved formats expose
            // a single channel pointer holding all samples interleaved.
            var dst = b.Pcm.Int16ChannelData;
            if (dst != IntPtr.Zero)
            {
                System.Buffer.MemoryCopy((void*)pcm, ((short**)dst)[0], frameCapacity * 2 * channels, bufferSize);
            }
            b.Pcm.FrameLength = frameCapacity;
        }

        // -- Source --------------------------------------------------------------

        public static Source SourceCreate(Listener listener, int sampleRate, int maxNumberOfBuffers, bool mono, bool spatialized, bool streamed, bool hrtf, float hrtfDirectionFactor, HrtfEnvironment environment)
        {
            var l = ResolveHandle<ManagedListener>(listener.Ptr);
            if (l?.Device == null) return default;
            var dev = l.Device;

            var src = new ManagedSource
            {
                Device = dev,
                Listener = l,
                SampleRate = sampleRate,
                Mono = mono,
                Spatialized = spatialized,
                Streamed = streamed,
            };
            uint channels = mono ? 1u : 2u;
            src.Format = new AVAudioFormat(AVAudioCommonFormat.PCMInt16, sampleRate, channels, interleaved: true);
            src.Player = new AVAudioPlayerNode();
            dev.Engine.AttachNode(src.Player);

            // 3D-spatialized mono sources route through the environment node (lazy-created);
            // everything else goes straight to the main mixer.
            if (spatialized && mono)
            {
                EnsureEnv(dev);
                dev.Engine.Connect(src.Player, dev.Env, src.Format);
            }
            else
            {
                dev.Engine.Connect(src.Player, dev.Mixer, src.Format);
            }

            // DynamicSoundSource owns and allocates the exact native pool. Extra
            // source-local buffers would have no matching managed pool tokens.

            lock (dev.Sources) dev.Sources.Add(src);
            return new Source { Ptr = AllocHandle(src) };
        }

        public static void SourceDestroy(Source source)
        {
            var src = ResolveHandle<ManagedSource>(source.Ptr);
            if (src != null) DestroyNativeSourceResources(src);
            FreeHandle(source.Ptr);
        }

        internal static bool SupportsStreamDeviceRecovery => false;
        internal static bool TryPrepareStreamRecovery(Source original, Source replacement, Buffer[] originalBuffers, Buffer[] replacementBuffers, Buffer[] managedFreeBuffers) => false;
        internal static int GetRecoveryPlaybackState(Source source) => throw new NotSupportedException("This backend has no streamed playback recovery adapter.");

        // No managed seek adapter is installed until the AVAudioPlayerNode segment
        // scheduler supports preserving both the first-pass offset and loop range.
        internal static bool TrySetPlaybackPosition(Source source, double seconds)
        {
            var src = ResolveHandle<ManagedSource>(source.Ptr);
            return seconds == 0 && CanPreserveStaticPlaybackRange(source) && src?.StaticBuffer?.Pcm != null;
        }
        internal static bool CanPreserveStaticPlaybackRange(Source source)
        {
            var src = ResolveHandle<ManagedSource>(source.Ptr);
            if (src == null || src.Streamed || src.StaticBuffer?.Pcm == null || src.SampleRate <= 0) return false;
            // The existing native buffer scheduler supports the full buffer only.
            // Do not report a trimmed segment restored until actual PCM segment
            // scheduling and subsequent full-range loop scheduling are installed.
            var duration = (double)src.StaticBuffer.Pcm.FrameLength / src.SampleRate;
            return src.PlayRangeStart == 0 && (src.PlayRangeStop <= 0 || src.PlayRangeStop >= duration);
        }
        internal static bool? TryGetDeviceInvalidation(Device device)
        {
            var native = ResolveHandle<ManagedDevice>(device.Ptr);
            return native?.Engine == null ? true : (bool?)null;
        }

        public static double SourceGetPosition(Source source)
        {
            var src = ResolveHandle<ManagedSource>(source.Ptr);
            if (src?.Player?.LastRenderTime is { } rt && src.SampleRate > 0)
            {
                var playerTime = src.Player.GetPlayerTimeFromNodeTime(rt);
                if (playerTime != null) return (double)playerTime.SampleTime / src.SampleRate;
            }
            return 0;
        }

        public static void SourceSetPan(Source source, float pan)
        {
            var src = ResolveHandle<ManagedSource>(source.Ptr);
            if (src?.Player != null) src.Player.Pan = pan;
        }

        public static void SourceSetBuffer(Source source, Buffer buffer)
        {
            var src = ResolveHandle<ManagedSource>(source.Ptr);
            var buf = ResolveHandle<ManagedBuffer>(buffer.Ptr);
            if (src == null) return;
            src.StaticBuffer = buf;
            src.StaticBufferScheduled = false;
        }

        public static void SourceFlushBuffers(Source source)
        {
            var src = ResolveHandle<ManagedSource>(source.Ptr);
            if (src?.Player == null) return;
            src.Player.Stop();
            src.StaticBufferScheduled = false;
            ReleaseNativeSchedules(src, disposing: false);
        }

        public static void SourceQueueBuffer(Source source, Buffer buffer, IntPtr pcm, int bufferSize, BufferType streamType)
        {
            var src = ResolveHandle<ManagedSource>(source.Ptr);
            var buf = ResolveHandle<ManagedBuffer>(buffer.Ptr);
            if (src?.Player == null || buf == null) return;
            BufferFill(buffer, pcm, bufferSize, src.SampleRate, src.Mono);
            if (buf.Pcm == null) throw new InvalidOperationException("The native audio packet has no PCM storage.");
            ScheduleNativePacket(src, buf, streamType);
        }

        public static Buffer SourceGetFreeBuffer(Source source)
        {
            var src = ResolveHandle<ManagedSource>(source.Ptr);
            if (src == null) return default;
            lock (src.ScheduleGate)
            {
                RetirePlayedNativeSchedules(src);
                if (src.StreamingFree.Count == 0) return default;
                var b = src.StreamingFree.Dequeue();
                return new Buffer { Ptr = b.NativeHandle };
            }
        }

        public static void SourcePlay(Source source)
        {
            var src = ResolveHandle<ManagedSource>(source.Ptr);
            if (src?.Player == null) return;
            // Static (non-streamed) sources schedule the single attached buffer here. The streaming
            // path schedules via SourceQueueBuffer; SourcePlay just resumes the player node.
            if (!src.Streamed && src.StaticBuffer?.Pcm != null && !src.StaticBufferScheduled)
            {
                var options = src.Looping ? AVAudioPlayerNodeBufferOptions.Loops : AVAudioPlayerNodeBufferOptions.Interrupts;
                src.Player.ScheduleBuffer(src.StaticBuffer.Pcm, when: null, options, completionHandler: () => { });
                src.StaticBufferScheduled = true;
            }
            src.Player.Play();
        }

        public static void SourcePause(Source source)
        {
            var src = ResolveHandle<ManagedSource>(source.Ptr);
            src?.Player?.Pause();
        }

        public static void SourceStop(Source source)
        {
            var src = ResolveHandle<ManagedSource>(source.Ptr);
            src?.Player?.Stop();
            if (src != null)
                src.StaticBufferScheduled = false;
            if (src != null)
            {
                ReleaseNativeSchedules(src, disposing: false);
            }
        }

        public static void SourceSetLooping(Source source, bool looped)
        {
            var src = ResolveHandle<ManagedSource>(source.Ptr);
            if (src != null) src.Looping = looped;
        }

        public static void SourceSetRange(Source source, double startTime, double stopTime)
        {
            var src = ResolveHandle<ManagedSource>(source.Ptr);
            if (src != null)
            {
                src.PlayRangeStart = startTime;
                src.PlayRangeStop = stopTime;
            }
        }

        public static void SourceSetGain(Source source, float gain)
        {
            var src = ResolveHandle<ManagedSource>(source.Ptr);
            if (src != null)
            {
                src.Gain = gain;
                if (src.Player != null) src.Player.Volume = gain;
            }
        }

        public static void SourceSetPitch(Source source, float pitch)
        {
            // AVAudioPlayerNode has no rate property; an AVAudioUnitVarispeed node between player
            // and mixer would handle this. Skipped until a real consumer needs it.
        }

        public static void SourcePush3D(Source source, ref Vector3 pos, ref Vector3 forward, ref Vector3 up, ref Vector3 vel, ref Matrix worldTransform)
        {
            var src = ResolveHandle<ManagedSource>(source.Ptr);
            if (src?.Player == null) return;
            if (src.Spatialized)
            {
                src.Player.Position = new System.Numerics.Vector3(pos.X, pos.Y, pos.Z);
            }
        }

        public static bool SourceIsPlaying(Source source)
        {
            var src = ResolveHandle<ManagedSource>(source.Ptr);
            return src?.Player?.Playing ?? false;
        }
    }
}
#endif
