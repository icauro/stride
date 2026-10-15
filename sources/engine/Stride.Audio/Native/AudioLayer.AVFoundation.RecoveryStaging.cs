#if STRIDE_PLATFORM_IOS || STRIDE_PLATFORM_MACOS
using System;
using System.Collections.Generic;
using System.Threading;
using AVFoundation;
using Foundation;

namespace Stride.Audio
{
    public partial class AudioLayer
    {
        private sealed partial class ManagedDevice
        {
            internal NativeDeviceRouteObserver RecoveryRouteObserver;
        }
        private sealed partial class ManagedSource
        {
            internal NativeRecoveredSchedule RecoverySchedule;
            internal readonly List<NativeAudioScheduleSnapshot> RecoverySnapshots = new();
        }
        // This owner is bound to the actual candidate source, not a playback
        // journal. SourceDestroy owns every scheduled PCM packet after publication.
        private sealed class NativeRecoveredSchedule : IDisposable
        {
            internal readonly ManagedSource Source;
            internal long PositionFrame, RangeStart, RangeEnd;
            internal bool Looping, Static, Published;
            internal NativeRecoveredSchedule(ManagedSource source) { Source = source; }
            internal bool TryGetPosition(out double seconds)
            {
                seconds = 0;
                if (!TryReadNativeSampleClock(Source, out var frame)) return false;
                var absolute = checked(PositionFrame + frame);
                if (Static)
                {
                    if (Looping && RangeEnd > RangeStart && absolute >= RangeEnd)
                        absolute = RangeStart + (absolute - RangeEnd) % (RangeEnd - RangeStart);
                    else if (absolute > RangeEnd) absolute = RangeEnd;
                }
                seconds = (double)absolute / Source.SampleRate;
                return true;
            }
            internal void Publish() { Published = true; Source.RecoverySchedule = this; }
            public void Dispose()
            {
                if (Published) return;
                Source.Player.Stop();
                ReleaseNativeSchedules(Source, disposing: false);
                Source.StaticBufferScheduled = false;
            }
        }
        private static unsafe AVAudioPcmBuffer CopyNativePcmRange(ManagedSource source, AVAudioPcmBuffer pcm, uint start, uint count)
        {
            if (pcm == null || start > pcm.FrameLength || count == 0 || count > pcm.FrameLength - start
                || pcm.Int16ChannelData == IntPtr.Zero) throw new InvalidOperationException("The real native PCM range is unavailable.");
            var result = new AVAudioPcmBuffer(source.Format, count);
            try
            {
                if (result.Int16ChannelData == IntPtr.Zero) throw new InvalidOperationException("The native PCM range could not be allocated.");
                var channels = source.Mono ? 1 : 2;
                var bytes = checked((long)count * channels * sizeof(short));
                var offset = checked((long)start * channels);
                System.Buffer.MemoryCopy(((short**)pcm.Int16ChannelData)[0] + offset,
                    ((short**)result.Int16ChannelData)[0], bytes, bytes);
                result.FrameLength = count; return result;
            }
            catch { result.Dispose(); throw; }
        }
        private static void ScheduleRecoveryPacket(ManagedSource source, AVAudioPcmBuffer pcm, ManagedBuffer pool,
            BufferType type, bool poolAlreadyReturned, bool looping)
        {
            lock (source.ScheduleGate)
            {
                var record = new NativeAudioSchedule { Pcm = pcm, Pool = pool, Type = type,
                    Generation = pool == null ? 0 : ++pool.ScheduleGeneration,
                    PoolReturned = pool == null || poolAlreadyReturned,
                    StartFrame = source.ScheduledFrames, EndFrame = checked(source.ScheduledFrames + (long)pcm.FrameLength) };
                source.NativeSchedules.Add(record);
                if (pool != null && !poolAlreadyReturned) pool.Scheduled = true;
                try
                {
                    source.Player.ScheduleBuffer(pcm, null,
                        looping ? AVAudioPlayerNodeBufferOptions.Loops : (AVAudioPlayerNodeBufferOptions)0,
                        AVAudioPlayerNodeCompletionCallbackType.PlayedBack, _ =>
                        {
                            lock (source.ScheduleGate)
                            {
                                record.Consumed = true;
                                // The clock and real played-back handshake jointly
                                // retire this retained packet and return its pool.
                            }
                        });
                    source.ScheduledFrames = record.EndFrame;
                }
                catch
                {
                    source.NativeSchedules.Remove(record); pcm.Dispose(); throw;
                }
            }
        }
        private static NativeRecoveredSchedule StageNativeStaticSchedule(ManagedSource candidate, double positionSeconds)
        {
            if (candidate == null || candidate.Streamed || candidate.Player.Playing || candidate.StaticBufferScheduled || candidate.NativeSchedules.Count != 0
                || candidate.StaticBuffer?.Pcm == null || candidate.SampleRate <= 0 || !double.IsFinite(positionSeconds))
                throw new NotSupportedException("The native static source is not an unused recovery candidate.");
            var total = (long)candidate.StaticBuffer.Pcm.FrameLength;
            var start = checked((long)Math.Floor(candidate.PlayRangeStart * candidate.SampleRate));
            var end = candidate.PlayRangeStop <= 0 ? total : checked((long)Math.Floor(candidate.PlayRangeStop * candidate.SampleRate));
            var position = checked((long)Math.Floor(positionSeconds * candidate.SampleRate));
            if (start < 0 || end > total || end <= start || position < start || position >= end)
                throw new NotSupportedException("The real native playback position is outside the authored PCM range.");
            var stage = new NativeRecoveredSchedule(candidate) { Static = true, PositionFrame = position,
                RangeStart = start, RangeEnd = end, Looping = candidate.Looping };
            try
            {
                ScheduleRecoveryPacket(candidate, CopyNativePcmRange(candidate, candidate.StaticBuffer.Pcm,
                    checked((uint)position), checked((uint)(end - position))), null, BufferType.None, true, false);
                if (candidate.Looping)
                    ScheduleRecoveryPacket(candidate, CopyNativePcmRange(candidate, candidate.StaticBuffer.Pcm,
                        checked((uint)start), checked((uint)(end - start))), null, BufferType.EndOfLoop, true, true);
                candidate.StaticBufferScheduled = true;
                return stage;
            }
            catch { stage.Dispose(); throw; }
        }
        private static NativeRecoveredSchedule StageNativeStreamSchedule(ManagedSource original, ManagedSource candidate,
            Buffer[] originalBuffers, Buffer[] replacementBuffers, Buffer[] managedFreeBuffers)
        {
            if (original == null || candidate == null || !original.Streamed || !candidate.Streamed || candidate.Player.Playing
                || candidate.NativeSchedules.Count != 0 || original.SampleRate != candidate.SampleRate || original.Mono != candidate.Mono
                || originalBuffers.Length != replacementBuffers.Length || !TryCaptureNativeSchedule(original, out var snapshot))
                throw new NotSupportedException("The paused native stream has no certified sample clock and schedule.");
            candidate.RecoverySnapshots.Add(snapshot);
            try
            {
                var map = new Dictionary<ManagedBuffer, ManagedBuffer>();
                var actualPool = new HashSet<ManagedBuffer>();
                foreach (var buffer in replacementBuffers)
                {
                    var target = ResolveHandle<ManagedBuffer>(buffer.Ptr);
                    if (target == null || !actualPool.Add(target)) throw new InvalidOperationException("The native replacement pool is not unique.");
                }
                for (var i = 0; i < originalBuffers.Length; i++)
                {
                    var old = ResolveHandle<ManagedBuffer>(originalBuffers[i].Ptr);
                    var target = ResolveHandle<ManagedBuffer>(replacementBuffers[i].Ptr);
                    if (old == null || map.ContainsKey(old)) throw new InvalidOperationException("The native original pool is not unique.");
                    map.Add(old, target);
                }
                var classified = new HashSet<ManagedBuffer>();
                foreach (var buffer in managedFreeBuffers)
                {
                    var old = ResolveHandle<ManagedBuffer>(buffer.Ptr);
                    if (old == null || !map.ContainsKey(old) || !classified.Add(old))
                        throw new InvalidOperationException("The managed free pool does not match the actual native pool.");
                }
                lock (original.ScheduleGate)
                {
                    foreach (var free in original.StreamingFree)
                    {
                        if (!map.TryGetValue(free, out var target) || !classified.Add(free))
                            throw new InvalidOperationException("The native free pool has conflicting ownership.");
                        candidate.StreamingFree.Enqueue(target);
                    }
                    foreach (var record in snapshot.Remaining)
                    {
                        if (!map.ContainsKey(record.Pool) || !record.PoolReturned && !classified.Add(record.Pool))
                            throw new InvalidOperationException("The pending native packet has conflicting pool ownership.");
                    }
                    if (classified.Count != originalBuffers.Length)
                        throw new InvalidOperationException("The native pool has an unclassified owner.");
                }
                if (original.RecoverySchedule != null && !original.RecoverySchedule.TryGetPosition(out var seconds))
                    throw new NotSupportedException("The prior native recovery generation has no certified sample clock.");
                var baseFrame = original.RecoverySchedule == null ? snapshot.SampleFrame
                    : checked(original.RecoverySchedule.PositionFrame + snapshot.SampleFrame);
                var stage = new NativeRecoveredSchedule(candidate) { PositionFrame = baseFrame };
                try
                {
                    foreach (var record in snapshot.Remaining)
                    {
                        var skipped = Math.Max(0, snapshot.SampleFrame - record.StartFrame);
                        var count = checked((long)record.Pcm.FrameLength - skipped);
                        ScheduleRecoveryPacket(candidate, CopyNativePcmRange(candidate, record.Pcm,
                            checked((uint)skipped), checked((uint)count)), map[record.Pool], record.Type, record.PoolReturned, false);
                    }
                    return stage;
                }
                catch { stage.Dispose(); throw; }
            }
            finally
            {
                snapshot.Dispose(); candidate.RecoverySnapshots.Remove(snapshot);
            }
        }
        private static void ReleaseNativeRecoverySnapshots(ManagedSource source)
        {
            while (source.RecoverySnapshots.Count != 0)
            {
                source.RecoverySnapshots[0].Dispose(); source.RecoverySnapshots.RemoveAt(0);
            }
        }
        private static NativeDeviceRouteObserver StageNativeDeviceRouteObserver(ManagedDevice device)
        {
            // Retain the partially attached observer on the actual native owner
            // before registration can fail; source teardown can retry its release.
            var observer = device.RecoveryRouteObserver ??= new NativeDeviceRouteObserver(device);
            observer.Attach(); return observer;
        }
        private static void ReleaseNativeDeviceRouteObserver(ManagedDevice device)
        {
            if (device.RecoveryRouteObserver == null) return;
            device.RecoveryRouteObserver.Dispose(); device.RecoveryRouteObserver = null;
        }
        private sealed class NativeDeviceRouteObserver : IDisposable
        {
            private readonly ManagedDevice device;
            private NSObject engineObserver, routeObserver;
            private bool routeRemoved;
            private int invalidated, disposing;
            internal NativeDeviceRouteObserver(ManagedDevice device) { this.device = device; }
            internal void Attach()
            {
                if (Volatile.Read(ref disposing) != 0) throw new ObjectDisposedException(nameof(NativeDeviceRouteObserver));
                try
                {
                    engineObserver ??= AVAudioEngine.Notifications.ObserveConfigurationChange(device.Engine,
                        (_, args) => { if (Volatile.Read(ref disposing) == 0) Interlocked.Exchange(ref invalidated, 1); });
#if STRIDE_PLATFORM_IOS
                    routeObserver ??= NSNotificationCenter.DefaultCenter.AddObserver(AVAudioSession.RouteChangeNotification,
                        notification => { if (Volatile.Read(ref disposing) == 0) Interlocked.Exchange(ref invalidated, 1); }, AVAudioSession.SharedInstance());
#endif
                }
                catch { throw; }
            }
            internal bool IsInvalidated => device.Engine == null || Volatile.Read(ref invalidated) != 0;
            public void Dispose()
            {
                Interlocked.Exchange(ref disposing, 1);
                if (engineObserver != null) { engineObserver.Dispose(); engineObserver = null; }
                if (routeObserver != null)
                {
                    if (!routeRemoved) { NSNotificationCenter.DefaultCenter.RemoveObserver(routeObserver); routeRemoved = true; }
                    routeObserver.Dispose(); routeObserver = null;
                }
            }
        }
    }
}
#endif
