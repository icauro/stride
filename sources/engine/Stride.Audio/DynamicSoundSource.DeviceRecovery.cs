using System;
using System.Collections.Generic;
using System.Linq;

namespace Stride.Audio
{
    public abstract partial class DynamicSoundSource
    {
        // Custom providers may opt in when their device-bound resources consist
        // solely of the framework Source and deviceBuffers. Decoder state stays
        // on this same provider; native queued PCM and current sample offset move.
        protected virtual bool SupportsNativeBufferDeviceRecreation => false;
        internal bool CanPreserveDeviceBuffers => SupportsNativeBufferDeviceRecreation && !isDisposed
            && !disposalRequested && deviceBuffers.Count != 0 && ReferenceEquals(soundInstance.DynamicSoundSource, this)
            && AudioLayer.SupportsStreamDeviceRecovery;
        private int nativeDeviceGeneration;
        internal sealed class DeviceBufferReplacement
        {
            internal AudioLayer.Buffer[] Original = Array.Empty<AudioLayer.Buffer>();
            internal AudioLayer.Buffer[] Replacement = Array.Empty<AudioLayer.Buffer>();
            internal AudioLayer.Buffer[] ManagedFree = Array.Empty<AudioLayer.Buffer>();
        }
        internal DeviceBufferReplacement PrepareDeviceBuffers(AudioLayer.Source replacementSource)
        {
            if (!CanPreserveDeviceBuffers) throw new NotSupportedException("This stream has no native buffered-audio device recreation adapter.");
            var original = deviceBuffers.ToArray();
            var replacements = new List<AudioLayer.Buffer>();
            try
            {
                foreach (var buffer in original)
                {
                    var replacement = AudioLayer.BufferCreate(nativeBufferSizeBytes);
                    if (replacement.Ptr == IntPtr.Zero) throw new InvalidOperationException("The replacement audio streaming buffer could not be allocated.");
                    replacements.Add(replacement);
                }
                var managedFree = freeBuffers.ToArray();
                var prepared = replacements.ToArray();
                if (!AudioLayer.TryPrepareStreamRecovery(soundInstance.Source, replacementSource, original, prepared, managedFree))
                    throw new InvalidOperationException("The native backend could not preserve the stream's queued PCM and sample position.");
                var indices = original.Select((buffer, index) => (buffer.Ptr, Index: index)).ToDictionary(pair => pair.Ptr, pair => pair.Index);
                return new() { Original = original, Replacement = prepared,
                    ManagedFree = managedFree.Select(buffer => prepared[indices[buffer.Ptr]]).ToArray() };
            }
            catch (Exception error)
            {
                // The staged source still borrows these buffers. Its owner releases
                // that voice first and then releases every allocated pool buffer.
                throw new DeviceBufferPreparationException(replacements.ToArray(), error);
            }
        }
        internal sealed class DeviceBufferPreparationException : InvalidOperationException
        {
            internal readonly AudioLayer.Buffer[] Buffers;
            internal DeviceBufferPreparationException(AudioLayer.Buffer[] buffers, Exception error)
                : base("The native stream device recreation preparation failed.", error) { Buffers = buffers; }
        }
        internal void PublishDeviceBuffers(DeviceBufferReplacement replacement)
        {
            deviceBuffers.Clear(); deviceBuffers.AddRange(replacement.Replacement);
            freeBuffers.Clear(); foreach (var buffer in replacement.ManagedFree) freeBuffers.Enqueue(buffer);
            nativeDeviceGeneration++;
            ResetDeviceClock();
            // Commands, decoder position, loop/range, actual readiness and EOF tasks
            // are preserved because the native pending PCM queue was copied intact.
        }
        internal virtual void ResetDeviceClock() { }
        internal void ResumePendingDevicePlay() => PlayAsyncInternal();
    }
}
