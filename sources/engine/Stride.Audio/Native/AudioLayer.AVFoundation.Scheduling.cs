#if STRIDE_PLATFORM_IOS || STRIDE_PLATFORM_MACOS
using System;
using System.Collections.Generic;
using AVFoundation;

namespace Stride.Audio
{
    public partial class AudioLayer
    {
        // ScheduleBuffer's existing completion overload reports data consumption.
        // Retain immutable native packets separately until the real sample clock
        // has passed them; a consumed pool buffer may already have been reused.
        private sealed class NativeAudioSchedule
        {
            internal ManagedBuffer Pool;
            internal AVAudioPcmBuffer Pcm;
            internal long Generation, StartFrame, EndFrame;
            internal BufferType Type;
            internal bool Consumed, PoolReturned;
        }
        private sealed class NativeAudioScheduleSnapshot : IDisposable
        {
            internal long SampleFrame;
            internal NativeAudioSchedule[] Remaining = Array.Empty<NativeAudioSchedule>();
            private int released;
            public void Dispose()
            {
                while (released < Remaining.Length) { Remaining[released].Pcm.Dispose(); released++; }
            }
        }
        private static unsafe AVAudioPcmBuffer CopyNativePcm(ManagedSource source, AVAudioPcmBuffer pcm)
        {
            if (pcm.FrameLength == 0 || pcm.Int16ChannelData == IntPtr.Zero)
                throw new InvalidOperationException("The native audio packet cannot be copied losslessly.");
            var copy = new AVAudioPcmBuffer(source.Format, pcm.FrameLength);
            try
            {
                if (copy.Int16ChannelData == IntPtr.Zero) throw new InvalidOperationException("The native audio packet could not be allocated.");
                var bytes = checked((long)pcm.FrameLength * sizeof(short) * (source.Mono ? 1 : 2));
                System.Buffer.MemoryCopy(((short**)pcm.Int16ChannelData)[0], ((short**)copy.Int16ChannelData)[0], bytes, bytes);
                copy.FrameLength = pcm.FrameLength;
                return copy;
            }
            catch { copy.Dispose(); throw; }
        }
        private static void ReturnNativePool(ManagedSource source, NativeAudioSchedule record)
        {
            if (record.PoolReturned) return;
            record.PoolReturned = true;
            if (source.ScheduleDisposed || record.Pool.NativeHandle == IntPtr.Zero
                || record.Pool.ScheduleGeneration != record.Generation) return;
            record.Pool.Scheduled = false;
            source.StreamingFree.Enqueue(record.Pool);
        }
        private static void ScheduleNativePacket(ManagedSource source, ManagedBuffer pool, BufferType type)
        {
            lock (source.ScheduleGate)
            {
                if (source.ScheduleDisposed) throw new ObjectDisposedException(nameof(ManagedSource));
                RetirePlayedNativeSchedules(source);
                var pcm = CopyNativePcm(source, pool.Pcm);
                var record = new NativeAudioSchedule { Pool = pool, Pcm = pcm, Type = type,
                    Generation = ++pool.ScheduleGeneration, StartFrame = source.ScheduledFrames,
                    EndFrame = checked(source.ScheduledFrames + (long)pcm.FrameLength) };
                source.NativeSchedules.Add(record);
                pool.StreamType = type; pool.Scheduled = true;
                try
                {
                    source.Player.ScheduleBuffer(pcm, () =>
                    {
                        lock (source.ScheduleGate)
                        {
                            record.Consumed = true;
                            // Consumption alone does not mean playback has passed
                            // this packet. The actual sample clock releases the pool.
                        }
                    });
                    source.ScheduledFrames = record.EndFrame;
                }
                catch
                {
                    source.NativeSchedules.Remove(record);
                    ReturnNativePool(source, record);
                    pcm.Dispose(); throw;
                }
            }
        }
        private static bool TryReadNativeSampleClock(ManagedSource source, out long frame)
        {
            frame = 0;
            if (source.Player?.LastRenderTime is not { } renderTime || source.SampleRate <= 0) return false;
            var playerTime = source.Player.GetPlayerTimeFromNodeTime(renderTime);
            if (playerTime == null || playerTime.SampleTime < 0 || playerTime.SampleRate != source.SampleRate) return false;
            frame = playerTime.SampleTime;
            return true;
        }
        private static void RetirePlayedNativeSchedules(ManagedSource source)
        {
            if (!TryReadNativeSampleClock(source, out var frame)) return;
            while (source.NativeSchedules.Count != 0)
            {
                var record = source.NativeSchedules[0];
                if (!record.Consumed || record.EndFrame > frame) return;
                ReturnNativePool(source, record);
                record.Pcm.Dispose(); source.NativeSchedules.RemoveAt(0);
            }
        }
        // This is a native scheduling certificate, not a device-recovery flag.
        // Staging still requires copied remaining packets, first-packet trimming,
        // timeline/loop origin and actual pool ownership publication as one unit.
        private static bool TryCaptureNativeSchedule(ManagedSource source, out NativeAudioScheduleSnapshot snapshot)
        {
            snapshot = null;
            lock (source.ScheduleGate)
            {
                if (source.ScheduleDisposed || !source.Streamed || source.Player.Playing
                    || !TryReadNativeSampleClock(source, out var frame)) return false;
                RetirePlayedNativeSchedules(source);
                var remaining = new List<NativeAudioSchedule>();
                try
                {
                    long previousEnd = frame;
                    foreach (var record in source.NativeSchedules)
                    {
                        if (record.EndFrame <= frame) continue;
                        if (record.Pcm == null || record.Pcm.Int16ChannelData == IntPtr.Zero
                            || record.StartFrame > previousEnd || record.EndFrame <= record.StartFrame)
                            throw new InvalidOperationException("The paused native audio schedule is not contiguous.");
                        remaining.Add(new() { Pool = record.Pool, Pcm = CopyNativePcm(source, record.Pcm),
                            Generation = record.Generation, StartFrame = record.StartFrame, EndFrame = record.EndFrame,
                            Type = record.Type, Consumed = record.Consumed, PoolReturned = record.PoolReturned });
                        previousEnd = record.EndFrame;
                    }
                    snapshot = new() { SampleFrame = frame, Remaining = remaining.ToArray() };
                    return true;
                }
                catch
                {
                    foreach (var record in remaining) record.Pcm.Dispose();
                    throw;
                }
            }
        }

        private static void DestroyNativeSourceResources(ManagedSource source)
        {
            lock (source.ScheduleGate) source.ScheduleDisposed = true;
            // A failed native release leaves the real source handle owned for
            // retry. Do not destroy retained PCM while a player may still read it.
            if (!source.PlayerStopped) { source.Player?.Stop(); source.PlayerStopped = true; }
            ReleaseNativeSchedules(source, disposing: true);
            if (!source.PlayerDetached)
            {
                if (source.Player != null && source.Device != null) source.Device.Engine.DetachNode(source.Player);
                source.PlayerDetached = true;
            }
            if (!source.PlayerReleased) { source.Player?.Dispose(); source.PlayerReleased = true; }
            if (!source.FormatReleased) { source.Format?.Dispose(); source.FormatReleased = true; }
            if (source.Device != null) lock (source.Device.Sources) source.Device.Sources.Remove(source);
        }
        private static void ReleaseNativeSchedules(ManagedSource source, bool disposing)
        {
            lock (source.ScheduleGate)
            {
                if (disposing) source.ScheduleDisposed = true;
                while (source.NativeSchedules.Count != 0)
                {
                    var record = source.NativeSchedules[0];
                    ReturnNativePool(source, record);
                    record.Pcm.Dispose(); source.NativeSchedules.RemoveAt(0);
                } source.ScheduledFrames = 0;
                if (disposing) source.StreamingFree.Clear();
            }
        }
    }
}
#endif
