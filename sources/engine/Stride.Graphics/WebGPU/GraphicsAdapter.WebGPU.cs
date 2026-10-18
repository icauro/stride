// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_GRAPHICS_API_WEBGPU
using System;
using System.Threading;
using Stride.Graphics.WebGPU;
namespace Stride.Graphics;
public partial class GraphicsAdapter
{
    private WebGpuDeviceContext preparedDevice;
    internal WebGpuDeviceContext TakePreparedDevice() => Interlocked.Exchange(ref preparedDevice, null);
    internal WGPUBackendType BackendType { get; }
    public string Description { get; }
    public int VendorId { get; }
    public bool IsDefaultAdapter => true;
    public bool IsSoftwareAdapter { get; }
    internal GraphicsAdapter(WebGpuDeviceContext context, bool retainDevice = false)
    {
        Description = context.AdapterName; BackendType = context.BackendType;
        VendorId = (int)context.VendorId; IsSoftwareAdapter = context.AdapterType == WGPUAdapterType.CPU;
        AdapterUid = ((long)context.VendorId << 32) | context.DeviceId;
        graphicsOutputs = Array.Empty<GraphicsOutput>();
        if (retainDevice) preparedDevice = context;
    }
    protected override void Destroy()
    {
        TakePreparedDevice()?.Dispose();
        base.Destroy();
    }
    public bool IsProfileSupported(GraphicsProfile profile) => profile >= GraphicsProfile.Level_9_1 && profile <= GraphicsProfile.Level_11_0;
}
#endif
