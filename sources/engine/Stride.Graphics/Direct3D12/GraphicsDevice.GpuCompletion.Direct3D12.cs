#if STRIDE_GRAPHICS_API_DIRECT3D12
using System;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using static Stride.Graphics.ComPtrHelpers;

namespace Stride.Graphics;

public unsafe partial class GraphicsDevice
{
    partial void QueryPhysicalDeviceUnavailable(ref bool unavailable)
        => unavailable = nativeDevice is null || nativeDevice->GetDeviceRemovedReason() < 0;
    partial void CreateGpuCompletionPlatform(ref GraphicsGpuCompletion? completion)
    {
        if (nativeDevice is null || nativeDevice->GetDeviceRemovedReason() < 0)
        { completion = new UnavailableGpuCompletion(this); return; }
        if (nativeCommandQueue is null || nativeCopyCommandQueue is null)
            throw new InvalidOperationException("The D3D12 queues are unavailable for a GPU completion marker.");
        ComPtr<ID3D12Fence> graphics = default, copy = default;
        try
        {
            var result = nativeDevice->CreateFence(InitialValue: 0, FenceFlags.None, out graphics);
            if (result.IsFailure) result.Throw();
            result = nativeDevice->CreateFence(InitialValue: 0, FenceFlags.None, out copy);
            if (result.IsFailure) result.Throw();
            lock (QueueLock)
            {
                // Independent fences: a later graphics value cannot falsely
                // certify completion of an earlier still-pending copy queue.
                result = nativeCopyCommandQueue->Signal(copy.Handle, 1);
                if (result.IsFailure) result.Throw();
                result = nativeCommandQueue->Signal(graphics.Handle, 1);
                if (result.IsFailure) result.Throw();
            }
            completion = new D3D12GpuCompletion(this, graphics, copy);
        }
        catch
        {
            SafeRelease(ref graphics); SafeRelease(ref copy);
            if (nativeDevice is null || nativeDevice->GetDeviceRemovedReason() < 0)
            { completion = new UnavailableGpuCompletion(this); return; }
            throw;
        }
    }

    private sealed class D3D12GpuCompletion : GraphicsGpuCompletion
    {
        private ComPtr<ID3D12Fence> graphics, copy;
        private readonly ID3D12Device* nativeGeneration;
        private bool disposed;
        private GraphicsGpuCompletionStatus status;
        internal D3D12GpuCompletion(GraphicsDevice device, ComPtr<ID3D12Fence> graphics, ComPtr<ID3D12Fence> copy) : base(device)
        { this.graphics = graphics; this.copy = copy; nativeGeneration = device.nativeDevice; }
        public override GraphicsGpuCompletionStatus Poll()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (status != GraphicsGpuCompletionStatus.Pending) return status;
            if (Device.nativeDevice != nativeGeneration)
            { Failure = "The D3D12 device generation changed before GPU completion was proved."; return status = GraphicsGpuCompletionStatus.Failed; }
            if (nativeGeneration->GetDeviceRemovedReason() < 0)
                return status = GraphicsGpuCompletionStatus.DeviceUnavailable;
            var graphicsValue = graphics.Handle->GetCompletedValue();
            var copyValue = copy.Handle->GetCompletedValue();
            if (graphicsValue == ulong.MaxValue || copyValue == ulong.MaxValue)
            { Failure = "D3D12 fence reported a removed-device sentinel before completion."; return status = GraphicsGpuCompletionStatus.Failed; }
            if (graphicsValue >= 1 && copyValue >= 1) status = GraphicsGpuCompletionStatus.Completed;
            return status;
        }
        public override void Dispose()
        { if (disposed) return; SafeRelease(ref graphics); SafeRelease(ref copy); disposed = true; }
    }
}
#endif
