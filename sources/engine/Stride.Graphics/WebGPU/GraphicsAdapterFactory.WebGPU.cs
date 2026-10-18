// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_GRAPHICS_API_WEBGPU
using System;
using Stride.Graphics.WebGPU;

namespace Stride.Graphics;

public static partial class GraphicsAdapterFactory
{
    private static void InitializeInternal()
    {
        staticCollector = new Stride.Core.ObjectCollector();
        var nativePath = Environment.GetEnvironmentVariable("STRIDE_WEBGPU_NATIVE_LIBRARY") ??
            throw new InvalidOperationException("Set STRIDE_WEBGPU_NATIVE_LIBRARY to the pinned native library's absolute path.");
        var backend = WGPUBackendType.Undefined;
        var backendName = Environment.GetEnvironmentVariable("STRIDE_WEBGPU_BACKEND");
        if (!string.IsNullOrEmpty(backendName) && (!Enum.TryParse(backendName, true, out backend) || backend is not (WGPUBackendType.Vulkan or WGPUBackendType.D3D12)))
            throw new ArgumentException("STRIDE_WEBGPU_BACKEND must be Vulkan or D3D12.");
        using var context = WebGpuDeviceContext.Create(nativePath, backend);
        defaultAdapter = new GraphicsAdapter(context);
        adapters = [defaultAdapter];
    }
}
#endif
