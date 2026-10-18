// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_GRAPHICS_API_WEBGPU
using System;
using Stride.Graphics.WebGPU;
using Api = Stride.Graphics.WebGPU.WebGPU;
namespace Stride.Graphics;

public unsafe partial class CommandList
{
    private void CopyTextureWithStencil(Texture source, WGPUTexelCopyTextureInfo location, WGPUTexelCopyTextureInfo target, WGPUExtent3D extent)
    {
        Api.wgpuCommandEncoderCopyTextureToTexture(encoder, &location, &target, &extent);
        if (!source.HasStencil || GraphicsDevice.WebGpuContext.BackendType != WGPUBackendType.Vulkan) return;
        // The pinned native Vulkan path on RX 6800 preserved depth but returned
        // stale stencil values in combined-aspect image copies. Copy stencil
        // explicitly through a GPU-only buffer; never expose opaque depth bits.
        var rowPitch = checked((extent.width + 255u) & ~255u);
        var descriptor = WGPUBufferDescriptor.CreateDefault();
        descriptor.size = checked((ulong)rowPitch * extent.height * extent.depthOrArrayLayers);
        descriptor.usage = WGPUBufferUsage.CopySrc | WGPUBufferUsage.CopyDst;
        var buffer = Api.wgpuDeviceCreateBuffer(GraphicsDevice.WebGpuContext.Device, &descriptor);
        try
        {
            GraphicsDevice.WebGpuContext.ThrowIfFailed();
            var copy = WGPUTexelCopyBufferInfo.CreateDefault(); copy.buffer = buffer;
            copy.layout.bytesPerRow = rowPitch; copy.layout.rowsPerImage = extent.height;
            location.aspect = WGPUTextureAspect.StencilOnly;
            target.aspect = WGPUTextureAspect.StencilOnly;
            Api.wgpuCommandEncoderCopyTextureToBuffer(encoder, &location, &copy, &extent);
            Api.wgpuCommandEncoderCopyBufferToTexture(encoder, &copy, &target, &extent);
            GraphicsDevice.WebGpuContext.ThrowIfFailed();
        }
        finally { if (buffer.Handle != 0) Api.wgpuBufferRelease(buffer); }
    }
}
#endif
