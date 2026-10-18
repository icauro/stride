// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_GRAPHICS_API_WEBGPU
using System;
using Stride.Graphics.WebGPU;
namespace Stride.Graphics;
public partial class GraphicsAdapter
{
    internal WGPUBackendType BackendType { get; }
    public string Description { get; }
    public int VendorId { get; }
    public bool IsDefaultAdapter => true;
    public bool IsSoftwareAdapter { get; }
    internal GraphicsAdapter(WebGpuDeviceContext context)
    {
        Description = context.AdapterName; BackendType = context.BackendType;
        VendorId = (int)context.VendorId; IsSoftwareAdapter = context.AdapterType == WGPUAdapterType.CPU;
        AdapterUid = ((long)context.VendorId << 32) | context.DeviceId;
        graphicsOutputs = Array.Empty<GraphicsOutput>();
    }
    public bool IsProfileSupported(GraphicsProfile profile) => profile >= GraphicsProfile.Level_9_1 && profile <= GraphicsProfile.Level_11_0;
}
#endif
