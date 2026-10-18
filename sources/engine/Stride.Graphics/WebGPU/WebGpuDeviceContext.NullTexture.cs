// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_GRAPHICS_API_WEBGPU
using System;
using Stride.Graphics.WebGPU;
using Api = Stride.Graphics.WebGPU.WebGPU;

namespace Stride.Graphics;

internal sealed unsafe partial class WebGpuDeviceContext
{
    private readonly object nullTextureGate = new();
    private WGPUTexture nullReadTexture;
    private WGPUTextureView nullReadTextureView;

    public WGPUTextureView NullReadTexture2D()
    {
        lock (nullTextureGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (nullReadTextureView.Handle != 0) return nullReadTextureView;
            var descriptor = WGPUTextureDescriptor.CreateDefault();
            descriptor.dimension = WGPUTextureDimension._2D;
            descriptor.size = new WGPUExtent3D { width = 1, height = 1, depthOrArrayLayers = 1 };
            descriptor.format = WGPUTextureFormat.RGBA8Unorm;
            descriptor.usage = WGPUTextureUsage.TextureBinding;
            descriptor.mipLevelCount = 1;
            descriptor.sampleCount = 1;
            try
            {
                // WebGPU initializes newly created textures to transparent zero.
                nullReadTexture = Api.wgpuDeviceCreateTexture(Device, &descriptor);
                ThrowIfFailed();
                nullReadTextureView = Api.wgpuTextureCreateView(nullReadTexture, null);
                ThrowIfFailed();
                return nullReadTextureView;
            }
            catch { ReleaseNullReadTexture(); throw; }
        }
    }

    private void ReleaseNullReadTexture()
    {
        lock (nullTextureGate)
        {
            if (nullReadTextureView.Handle != 0) Api.wgpuTextureViewRelease(nullReadTextureView);
            if (nullReadTexture.Handle != 0) Api.wgpuTextureRelease(nullReadTexture);
            nullReadTextureView = default;
            nullReadTexture = default;
        }
    }
}
#endif
