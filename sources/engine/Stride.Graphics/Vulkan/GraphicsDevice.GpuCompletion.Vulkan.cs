#if STRIDE_GRAPHICS_API_VULKAN
using System;
using Vortice.Vulkan;

namespace Stride.Graphics;

public partial class GraphicsDevice
{
    partial void QueryPhysicalDeviceUnavailable(ref bool unavailable)
        => unavailable = deviceLost || NativeDevice == VkDevice.Null;
    partial void CreateGpuCompletionPlatform(ref GraphicsGpuCompletion? completion)
    {
        if (deviceLost || NativeDevice == VkDevice.Null)
        { completion = new UnavailableGpuCompletion(this); return; }
        lock (QueueLock)
        {
            unsafe
            {
                var value = FrameFence.NextFenceValue++;
                var signal = SemaphoreSubmit(FrameFence.Semaphore, value);
                var submit = new VkSubmitInfo2
                {
                    sType = VkStructureType.SubmitInfo2,
                    signalSemaphoreInfoCount = 1,
                    pSignalSemaphoreInfos = &signal,
                };
                var result = NativeDeviceApi.vkQueueSubmit2(NativeCommandQueue, 1, &submit, VkFence.Null);
                if (result == VkResult.ErrorDeviceLost)
                { deviceLost = true; completion = new UnavailableGpuCompletion(this); return; }
                CheckResult(result);
                completion = new VulkanGpuCompletion(this, NativeDevice, FrameFence.Semaphore, value);
            }
        }
    }

    private sealed class VulkanGpuCompletion : GraphicsGpuCompletion
    {
        private readonly VkDevice nativeGeneration;
        private readonly VkSemaphore semaphore;
        private readonly ulong value;
        private bool disposed;
        private GraphicsGpuCompletionStatus status;
        internal VulkanGpuCompletion(GraphicsDevice device, VkDevice generation, VkSemaphore semaphore, ulong value) : base(device)
        { nativeGeneration = generation; this.semaphore = semaphore; this.value = value; }
        public override unsafe GraphicsGpuCompletionStatus Poll()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (status != GraphicsGpuCompletionStatus.Pending) return status;
            if (Device.NativeDevice != nativeGeneration || Device.FrameFence.Semaphore != semaphore)
            { Failure = "The Vulkan device/semaphore generation changed before GPU completion was proved."; return status = GraphicsGpuCompletionStatus.Failed; }
            if (Device.deviceLost)
                return status = GraphicsGpuCompletionStatus.DeviceUnavailable;
            ulong completed = 0;
            var result = Device.NativeDeviceApi.vkGetSemaphoreCounterValue(nativeGeneration, semaphore, &completed);
            if (result == VkResult.ErrorDeviceLost)
            { Device.deviceLost = true; return status = GraphicsGpuCompletionStatus.DeviceUnavailable; }
            if (result != VkResult.Success || completed == ulong.MaxValue)
            { Failure = $"Vulkan GPU completion query failed: {result}, counter {completed}."; return status = GraphicsGpuCompletionStatus.Failed; }
            if (completed >= value) status = GraphicsGpuCompletionStatus.Completed;
            return status;
        }
        // This token borrows the device's actual timeline semaphore; it never
        // destroys a semaphore while a submitted marker still references it.
        public override void Dispose() => disposed = true;
    }
}
#endif
