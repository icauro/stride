#if STRIDE_GRAPHICS_API_DIRECT3D11
using System;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
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
        if (nativeDeviceContext is null)
            throw new InvalidOperationException("The D3D11 device is unavailable for a GPU completion marker.");
        var description = new QueryDesc(Query.Event);
        ComPtr<ID3D11Query> query = default;
        HResult result = nativeDevice->CreateQuery(in description, ref query);
        if (result.IsFailure)
        {
            SafeRelease(ref query);
            if (nativeDevice->GetDeviceRemovedReason() < 0)
            { completion = new UnavailableGpuCompletion(this); return; }
            result.Throw();
        }
        try
        {
            nativeDeviceContext->End(query);
            nativeDeviceContext->Flush();
            completion = new D3D11GpuCompletion(this, query);
        }
        catch { query.Release(); throw; }
    }

    private sealed class D3D11GpuCompletion : GraphicsGpuCompletion
    {
        private ComPtr<ID3D11Query> query;
        private readonly ID3D11Device* nativeGeneration;
        private readonly ID3D11DeviceContext* contextGeneration;
        private bool disposed;
        private GraphicsGpuCompletionStatus status;
        internal D3D11GpuCompletion(GraphicsDevice device, ComPtr<ID3D11Query> query) : base(device)
        { this.query = query; nativeGeneration = device.nativeDevice; contextGeneration = device.nativeDeviceContext; }
        public override GraphicsGpuCompletionStatus Poll()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (status != GraphicsGpuCompletionStatus.Pending) return status;
            if (Device.nativeDevice != nativeGeneration || Device.nativeDeviceContext != contextGeneration)
            { Failure = "The D3D11 device generation changed before GPU completion was proved."; return status = GraphicsGpuCompletionStatus.Failed; }
            // A simulated reset does not prove the native queue unavailable.
            if (nativeGeneration->GetDeviceRemovedReason() < 0)
                return status = GraphicsGpuCompletionStatus.DeviceUnavailable;
            int completed = 0;
            // D3D11_ASYNC_GETDATA_DONOTFLUSH: the marker was already flushed once.
            HResult result = contextGeneration->GetData(query, ref completed, sizeof(int), GetDataFlags: 1);
            if (result.IsFailure)
            { Failure = $"D3D11 GPU completion GetData failed: {result}."; return status = GraphicsGpuCompletionStatus.Failed; }
            if (result == 0 && completed != 0) status = GraphicsGpuCompletionStatus.Completed;
            return status;
        }
        public override void Dispose()
        { if (disposed) return; query.Release(); disposed = true; }
    }
}
#endif
