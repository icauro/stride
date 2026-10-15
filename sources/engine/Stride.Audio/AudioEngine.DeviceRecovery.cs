using System;
using System.Collections.Generic;
using System.Linq;

namespace Stride.Audio
{
    public partial class AudioEngine
    {
        private readonly HashSet<AudioListener> deviceListeners = new HashSet<AudioListener>();
        private readonly HashSet<SoundInstance> deviceInstances = new HashSet<SoundInstance>();
        private readonly Queue<Action> deviceRecoveryCleanup = new Queue<Action>();
        private AudioEngineState stateBeforeInvalidation = AudioEngineState.Running;
        private bool deviceRecreationNotification;
        private int deviceOwnerThread;
        public long DeviceGeneration { get; private set; }
        public Exception DeviceRecoveryError { get; private set; }
        public bool DeviceRecoveryCleanupPending => deviceRecoveryCleanup.Count != 0;
        public event EventHandler DeviceRecreated;
        /// <summary>Returns null when the loaded native backend cannot certify its device connection state.</summary>
        public bool? CheckDeviceAvailability()
        {
            if (State == AudioEngineState.Disposed || State == AudioEngineState.Invalidated) return false;
            var invalidated = AudioLayer.TryGetDeviceInvalidation(AudioDevice);
            if (invalidated == true) { stateBeforeInvalidation = State; State = AudioEngineState.Invalidated; }
            return invalidated.HasValue ? !invalidated.Value : (bool?)null;
        }
        internal void RegisterDeviceListener(AudioListener listener) => deviceListeners.Add(listener);
        internal void UnregisterDeviceListener(AudioListener listener) => deviceListeners.Remove(listener);
        internal void RegisterDeviceInstance(SoundInstance instance) => deviceInstances.Add(instance);
        internal void UnregisterDeviceInstance(SoundInstance instance) => deviceInstances.Remove(instance);

        /// <summary>Stages real native resources and replaces their handles without replacing managed sound/listener/controller identities.</summary>
        /// <returns>False while the native streaming worker owns its current batch; retry on the owning game thread.</returns>
        public bool TryRecreateDevice(AudioDevice requestedDevice = null)
        {
            if (State == AudioEngineState.Disposed) throw new ObjectDisposedException(nameof(AudioEngine));
            if (Environment.CurrentManagedThreadId != deviceOwnerThread) throw new InvalidOperationException("Audio device recreation requires the owning game thread.");
            if (DefaultListener == null) throw new InvalidOperationException("The audio engine must be initialized before its device can be recreated.");
#if STRIDE_PLATFORM_IOS
            throw new NotSupportedException("This platform requires its shared-listener device recreation adapter.");
#else
            if (!DynamicSoundSource.TryAcquireNativeWorker(out var lease)) return false;
            using (lease)
            {
                try
                {
                    if (deviceRecoveryCleanup.Count != 0)
                    {
                        var wasPublished = deviceRecreationNotification;
                        FinishDeviceCleanup();
                        if (wasPublished) { lease.Dispose(); NotifyDeviceRecreated(); return true; }
                    }
                    DynamicSoundSource.DrainDisposedDeviceSources(this);
                    var instances = deviceInstances.Where(instance => !instance.IsDisposed).ToArray();
                    if (instances.Any(instance => !instance.CanRecreateDeviceSource))
                        throw new NotSupportedException("An active dynamic audio stream requires its native device recreation adapter.");
                    var playback = instances.Select(instance => instance.CaptureDevicePlayback()).ToArray();
                    foreach (var source in playback)
                        if (source.Original.Ptr != IntPtr.Zero && source.NativePlayback == 1) AudioLayer.SourcePause(source.Original);
                    var originalsPaused = true;
                    try
                    {
                    var listeners = deviceListeners.ToArray();
                    var buffers = new Dictionary<Sound, AudioLayer.Buffer>();
                    var listenerHandles = new Dictionary<AudioListener, AudioLayer.Listener>();
                    var replacements = new List<SoundInstance.DeviceSourceReplacement>();
                    var target = requestedDevice ?? audioDevice ?? new AudioDevice();
                    var candidate = AudioLayer.Create(target.Name == "default" ? null : target.Name, initializedDeviceFlags);
                    if (candidate.Ptr == IntPtr.Zero) throw new InvalidOperationException("The replacement audio device could not be opened.");
                    try
                    {
                        foreach (var listener in listeners)
                        {
                            var handle = AudioLayer.ListenerCreate(candidate);
                            if (handle.Ptr == IntPtr.Zero) throw new InvalidOperationException("The replacement audio listener could not be created.");
                            listenerHandles.Add(listener, handle);
                            if (!AudioLayer.ListenerEnable(handle)) throw new InvalidOperationException("The replacement audio listener could not be enabled.");
                            listener.UpdateDeviceListener(handle);
                        }
                        if (DefaultListener != null && listenerHandles.TryGetValue(DefaultListener, out var candidateDefault)) AudioLayer.ListenerEnable(candidateDefault);
                        foreach (var sound in notDisposedSounds.OfType<Sound>().Where(sound => !sound.IsDisposed && (sound.PreloadedBuffer.Ptr != IntPtr.Zero || instances.Any(instance => !instance.UsesDynamicDeviceSource && ReferenceEquals(instance.DeviceSound, sound)))))
                            buffers.Add(sound, sound.CreateDeviceBuffer());
                        foreach (var source in playback)
                        {
                            replacements.Add(source);
                            source.Owner.PrepareDeviceSource(listenerHandles[source.Owner.Listener],
                                source.Owner.UsesDynamicDeviceSource ? default : buffers[source.Owner.DeviceSound], source);
                        }
                        if (AudioLayer.TryGetDeviceInvalidation(candidate) == true)
                            throw new InvalidOperationException("The replacement audio device was invalidated while staging its native resources.");
                        AudioLayer.SetMasterVolume(candidate, masterVolume);
                    }
                    catch
                    {
                        foreach (var source in replacements)
                        {
                            if (source.Replacement.Ptr != IntPtr.Zero) deviceRecoveryCleanup.Enqueue(() => AudioLayer.SourceDestroy(source.Replacement));
                            if (source.Buffers != null)
                                foreach (var buffer in source.Buffers.Replacement)
                                    deviceRecoveryCleanup.Enqueue(() => AudioLayer.BufferDestroy(buffer));
                        }
                        var candidateContext = listenerHandles.GetValueOrDefault(DefaultListener);
                        foreach (var buffer in buffers.Values) deviceRecoveryCleanup.Enqueue(() =>
                        { if (candidateContext.Ptr != IntPtr.Zero) AudioLayer.ListenerEnable(candidateContext); AudioLayer.BufferDestroy(buffer); });
                        foreach (var listener in listenerHandles.Values) deviceRecoveryCleanup.Enqueue(() => AudioLayer.ListenerDestroy(listener));
                        deviceRecoveryCleanup.Enqueue(() => AudioLayer.Destroy(candidate));
                        try { FinishDeviceCleanup(); }
                        finally { if (DefaultListener != null && DefaultListener.Listener.Ptr != IntPtr.Zero) AudioLayer.ListenerEnable(DefaultListener.Listener); }
                        throw;
                    }
                    var previousDevice = AudioDevice;
                    var previousDefaultListener = DefaultListener.Listener;
                    var previousState = State == AudioEngineState.Invalidated ? stateBeforeInvalidation : State;
                    foreach (var source in replacements)
                    {
                        if (source.Original.Ptr != IntPtr.Zero) deviceRecoveryCleanup.Enqueue(() => AudioLayer.SourceDestroy(source.Original));
                        if (source.Buffers != null)
                            foreach (var buffer in source.Buffers.Original)
                                deviceRecoveryCleanup.Enqueue(() => AudioLayer.BufferDestroy(buffer));
                    }
                    foreach (var sound in buffers)
                    {
                        var original = sound.Key.PreloadedBuffer;
                        sound.Key.PreloadedBuffer = sound.Value;
                        if (original.Ptr != IntPtr.Zero) deviceRecoveryCleanup.Enqueue(() =>
                        { if (previousDefaultListener.Ptr != IntPtr.Zero) AudioLayer.ListenerEnable(previousDefaultListener); AudioLayer.BufferDestroy(original); });
                    }
                    foreach (var listener in listenerHandles)
                    {
                        var original = listener.Key.Listener;
                        listener.Key.Listener = listener.Value;
                        if (original.Ptr != IntPtr.Zero) deviceRecoveryCleanup.Enqueue(() => AudioLayer.ListenerDestroy(original));
                    }
                    AudioDevice = candidate; audioDevice = new AudioDevice { Name = target.Name }; State = previousState;
                    originalsPaused = false;
                    foreach (var source in replacements) source.Owner.PublishDeviceSource(source);
                    foreach (var listener in listeners) listener.Update();
                    if (previousDevice.Ptr != IntPtr.Zero) deviceRecoveryCleanup.Enqueue(() => AudioLayer.Destroy(previousDevice));
                    DeviceGeneration++; deviceRecreationNotification = true;
                    FinishDeviceCleanup(); AudioLayer.ListenerEnable(DefaultListener.Listener);
                    lease.Dispose(); NotifyDeviceRecreated(); return true;
                    }
                    finally
                    {
                        if (originalsPaused)
                            foreach (var source in playback)
                                if (source.Original.Ptr != IntPtr.Zero && source.NativePlayback == 1) AudioLayer.SourcePlay(source.Original);
                    }
                }
                catch (Exception error) { DeviceRecoveryError = error; throw; }
            }
#endif
        }
        private void FinishDeviceCleanup()
        {
            while (deviceRecoveryCleanup.Count != 0) { deviceRecoveryCleanup.Peek()(); deviceRecoveryCleanup.Dequeue(); }
            DeviceRecoveryError = null;
        }
        private void NotifyDeviceRecreated()
        {
            if (!deviceRecreationNotification) return;
            deviceRecreationNotification = false;
            DeviceRecreated?.Invoke(this, EventArgs.Empty);
        }
    }
}
