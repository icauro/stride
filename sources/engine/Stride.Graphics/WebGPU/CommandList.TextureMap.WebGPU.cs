// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_GRAPHICS_API_WEBGPU
using System;
using System.Collections.Generic;
using Stride.Graphics.WebGPU;
using Api = Stride.Graphics.WebGPU.WebGPU;
namespace Stride.Graphics;

public unsafe partial class CommandList
{
    private readonly Dictionary<GraphicsResource, TextureWriteMap> textureWriteMaps = new();
    private readonly record struct TextureWriteMap(WGPUBuffer Upload, int Subresource, int RowPitch, int Rows, int Length);

    private MappedResource MapDynamicTexture(Texture texture, int subresource, int offset, int length)
    {
        EnsureRecording();
        if (offset != 0 || length != 0) throw new ArgumentException("Texture map byte ranges are not supported.");
        if (texture.IsDepthTexture || texture.IsMultiSampled) throw new NotSupportedException("Dynamic texture maps require single-sample color textures.");
        var layout = texture.SubresourceLayout(subresource);
        var pitch = checked((int)layout.RowPitch);
        var slicePitch = checked(pitch * (int)layout.Rows);
        var size = checked(slicePitch * (int)layout.Depth);
        var descriptor = WGPUBufferDescriptor.CreateDefault();
        descriptor.size = (ulong)size; descriptor.usage = WGPUBufferUsage.CopySrc; descriptor.mappedAtCreation = 1;
        var upload = Api.wgpuDeviceCreateBuffer(GraphicsDevice.WebGpuContext.Device, &descriptor);
        try
        {
            GraphicsDevice.WebGpuContext.ThrowIfFailed();
            var pointer = Api.wgpuBufferGetMappedRange(upload, 0, (nuint)size);
            if (pointer == null) throw new InvalidOperationException("Dynamic texture upload returned a null mapped range.");
            textureWriteMaps.Add(texture, new TextureWriteMap(upload, subresource, pitch, (int)layout.Rows, size));
            return new MappedResource(texture, subresource, new DataBox((nint)pointer, pitch, slicePitch), 0, size);
        }
        catch { if (upload.Handle != 0) Api.wgpuBufferRelease(upload); throw; }
    }

    private void UnmapDynamicTexture(Texture texture, TextureWriteMap write)
    {
        try
        {
            Api.wgpuBufferUnmap(write.Upload);
            EnsureResource(texture); EnsureRecording();
            var source = WGPUTexelCopyBufferInfo.CreateDefault();
            source.buffer = write.Upload;
            source.layout.bytesPerRow = (uint)write.RowPitch; source.layout.rowsPerImage = (uint)write.Rows;
            var destination = texture.CopyLocation(write.Subresource);
            var extent = texture.CopyExtent(write.Subresource);
            Api.wgpuCommandEncoderCopyBufferToTexture(encoder, &source, &destination, &extent);
            GraphicsDevice.WebGpuContext.ThrowIfFailed();
        }
        finally { Api.wgpuBufferRelease(write.Upload); }
    }
}
#endif
