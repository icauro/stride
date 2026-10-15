// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
#if !(STRIDE_PLATFORM_IOS || STRIDE_PLATFORM_MACOS)
using System;
using System.Runtime.InteropServices;
using System.Security;
using Stride.Core.Mathematics;

namespace Stride.Audio
{
    /// <summary>
    /// libstrideaudio (OpenAL on desktop / OpenSL ES on Android / XAudio2 on Windows / static-linked OpenAL on iOS) backend.
    /// </summary>
    public partial class AudioLayer
    {
        static AudioLayer()
        {
            NativeInvoke.PreLoad();
        }

        [SuppressUnmanagedCodeSecurity]
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioInit", CallingConvention = CallingConvention.Cdecl)]
        public static extern bool Init();

        [SuppressUnmanagedCodeSecurity]
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioCreate", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        public static extern Device Create(string deviceName, DeviceFlags flags);

        [SuppressUnmanagedCodeSecurity]
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioDestroy", CallingConvention = CallingConvention.Cdecl)]
        public static extern void Destroy(Device device);

        [SuppressUnmanagedCodeSecurity]
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioUpdate", CallingConvention = CallingConvention.Cdecl)]
        public static extern void Update(Device device);

        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioRecoveryCapabilities", CallingConvention = CallingConvention.Cdecl)]
        private static extern int RecoveryCapabilities();
        private static int? recoveryCapabilities;
        internal static bool SupportsStreamDeviceRecovery
        {
            get
            {
                if (!recoveryCapabilities.HasValue)
                    try { recoveryCapabilities = RecoveryCapabilities(); }
                    catch (EntryPointNotFoundException) { recoveryCapabilities = 0; }
                return (recoveryCapabilities.Value & 2) != 0;
            }
        }
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioPrepareStreamRecovery", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool PrepareStreamRecovery(Source original, Source replacement,
            [In] Buffer[] originalBuffers, [In] Buffer[] replacementBuffers, int count,
            [In] Buffer[] managedFreeBuffers, int managedFreeCount);
        internal static bool TryPrepareStreamRecovery(Source original, Source replacement, Buffer[] originalBuffers, Buffer[] replacementBuffers, Buffer[] managedFreeBuffers)
            => SupportsStreamDeviceRecovery && originalBuffers.Length == replacementBuffers.Length
                && PrepareStreamRecovery(original, replacement, originalBuffers, replacementBuffers, originalBuffers.Length, managedFreeBuffers, managedFreeBuffers.Length);
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioSourceGetPlaybackState", CallingConvention = CallingConvention.Cdecl)]
        private static extern int NativePlaybackState(Source source);
        internal static int GetRecoveryPlaybackState(Source source)
        {
            if (!SupportsStreamDeviceRecovery) throw new NotSupportedException("The loaded native backend cannot capture streamed playback state.");
            var playback = NativePlaybackState(source);
            if (playback < 0 || playback > 2) throw new InvalidOperationException("The native streamed playback state could not be certified.");
            return playback;
        }

        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioSourceSetPlaybackPosition", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool SetPlaybackPosition(Source source, double seconds);
        internal static bool CanPreserveStaticPlaybackRange(Source source) => source.Ptr != IntPtr.Zero;
        private static bool? playbackPositionSupported;
        internal static bool TrySetPlaybackPosition(Source source, double seconds)
        {
            if (playbackPositionSupported == false) return false;
            try { var result = SetPlaybackPosition(source, seconds); playbackPositionSupported = true; return result; }
            catch (EntryPointNotFoundException) { playbackPositionSupported = false; return false; }
        }
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioDeviceIsInvalidated", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DeviceIsInvalidated(Device device);
        private static bool? deviceInvalidationSupported;
        internal static bool? TryGetDeviceInvalidation(Device device)
        {
            if (deviceInvalidationSupported == false) return null;
            try { var result = DeviceIsInvalidated(device); deviceInvalidationSupported = true; return result < 0 ? (bool?)null : result != 0; }
            catch (EntryPointNotFoundException) { deviceInvalidationSupported = false; return null; }
        }

        [SuppressUnmanagedCodeSecurity]
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioSetMasterVolume", CallingConvention = CallingConvention.Cdecl)]
        public static extern void SetMasterVolume(Device device, float volume);

        [SuppressUnmanagedCodeSecurity]
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioListenerCreate", CallingConvention = CallingConvention.Cdecl)]
        public static extern Listener ListenerCreate(Device device);

        [SuppressUnmanagedCodeSecurity]
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioListenerDestroy", CallingConvention = CallingConvention.Cdecl)]
        public static extern void ListenerDestroy(Listener listener);

        [SuppressUnmanagedCodeSecurity]
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioListenerEnable", CallingConvention = CallingConvention.Cdecl)]
        public static extern bool ListenerEnable(Listener listener);

        [SuppressUnmanagedCodeSecurity]
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioListenerDisable", CallingConvention = CallingConvention.Cdecl)]
        public static extern void ListenerDisable(Listener listener);

        [SuppressUnmanagedCodeSecurity]
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioSourceCreate", CallingConvention = CallingConvention.Cdecl)]
        public static extern Source SourceCreate(Listener listener, int sampleRate, int maxNumberOfBuffers, bool mono, bool spatialized, bool streamed, bool hrtf, float hrtfDirectionFactor, HrtfEnvironment environment);

        [SuppressUnmanagedCodeSecurity]
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioSourceDestroy", CallingConvention = CallingConvention.Cdecl)]
        public static extern void SourceDestroy(Source source);

        [SuppressUnmanagedCodeSecurity]
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioSourceGetPosition", CallingConvention = CallingConvention.Cdecl)]
        public static extern double SourceGetPosition(Source source);

        [SuppressUnmanagedCodeSecurity]
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioSourceSetPan", CallingConvention = CallingConvention.Cdecl)]
        public static extern void SourceSetPan(Source source, float pan);

        [SuppressUnmanagedCodeSecurity]
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioBufferCreate", CallingConvention = CallingConvention.Cdecl)]
        public static extern Buffer BufferCreate(int maxBufferSizeBytes);

        [SuppressUnmanagedCodeSecurity]
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioBufferDestroy", CallingConvention = CallingConvention.Cdecl)]
        public static extern void BufferDestroy(Buffer buffer);

        [SuppressUnmanagedCodeSecurity]
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioBufferFill", CallingConvention = CallingConvention.Cdecl)]
        public static extern void BufferFill(Buffer buffer, IntPtr pcm, int bufferSize, int sampleRate, bool mono);

        [SuppressUnmanagedCodeSecurity]
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioSourceSetBuffer", CallingConvention = CallingConvention.Cdecl)]
        public static extern void SourceSetBuffer(Source source, Buffer buffer);

        [SuppressUnmanagedCodeSecurity]
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioSourceFlushBuffers", CallingConvention = CallingConvention.Cdecl)]
        public static extern void SourceFlushBuffers(Source source);

        [SuppressUnmanagedCodeSecurity]
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioSourceQueueBuffer", CallingConvention = CallingConvention.Cdecl)]
        public static extern void SourceQueueBuffer(Source source, Buffer buffer, IntPtr pcm, int bufferSize, BufferType streamType);

        [SuppressUnmanagedCodeSecurity]
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioSourceGetFreeBuffer", CallingConvention = CallingConvention.Cdecl)]
        public static extern Buffer SourceGetFreeBuffer(Source source);

        [SuppressUnmanagedCodeSecurity]
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioSourcePlay", CallingConvention = CallingConvention.Cdecl)]
        public static extern void SourcePlay(Source source);

        [SuppressUnmanagedCodeSecurity]
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioSourcePause", CallingConvention = CallingConvention.Cdecl)]
        public static extern void SourcePause(Source source);

        [SuppressUnmanagedCodeSecurity]
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioSourceStop", CallingConvention = CallingConvention.Cdecl)]
        public static extern void SourceStop(Source source);

        [SuppressUnmanagedCodeSecurity]
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioSourceSetLooping", CallingConvention = CallingConvention.Cdecl)]
        public static extern void SourceSetLooping(Source source, bool looped);

        [SuppressUnmanagedCodeSecurity]
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioSourceSetRange", CallingConvention = CallingConvention.Cdecl)]
        public static extern void SourceSetRange(Source source, double startTime, double stopTime);

        [SuppressUnmanagedCodeSecurity]
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioSourceSetGain", CallingConvention = CallingConvention.Cdecl)]
        public static extern void SourceSetGain(Source source, float gain);

        [SuppressUnmanagedCodeSecurity]
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioSourceSetPitch", CallingConvention = CallingConvention.Cdecl)]
        public static extern void SourceSetPitch(Source source, float pitch);

        [SuppressUnmanagedCodeSecurity]
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioListenerPush3D", CallingConvention = CallingConvention.Cdecl)]
        public static extern void ListenerPush3D(Listener listener, ref Vector3 pos, ref Vector3 forward, ref Vector3 up, ref Vector3 vel, ref Matrix worldTransform);

        [SuppressUnmanagedCodeSecurity]
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioSourcePush3D", CallingConvention = CallingConvention.Cdecl)]
        public static extern void SourcePush3D(Source source, ref Vector3 pos, ref Vector3 forward, ref Vector3 up, ref Vector3 vel, ref Matrix worldTransform);

        [SuppressUnmanagedCodeSecurity]
        [DllImport(NativeInvoke.Library, EntryPoint = "xnAudioSourceIsPlaying", CallingConvention = CallingConvention.Cdecl)]
        public static extern bool SourceIsPlaying(Source source);
    }
}
#endif
