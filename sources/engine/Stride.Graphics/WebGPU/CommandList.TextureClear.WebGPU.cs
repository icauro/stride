// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_GRAPHICS_API_WEBGPU
using System;
using Stride.Core.Mathematics;
using Stride.Graphics.WebGPU;
using Api = Stride.Graphics.WebGPU.WebGPU;
namespace Stride.Graphics;

public unsafe partial class CommandList
{
    private void ClearTexture(Texture texture, UInt4 bits, Vector4? floats)
    {
        EnsureRecording(); EnsureResource(texture);
        if (!texture.IsUnorderedAccess || texture.Usage == GraphicsResourceUsage.Staging)
            throw new ArgumentException("Expecting a Texture view supporting UAV.", nameof(texture));
        if (texture.IsMultiSampled || texture.IsDepthTexture
            || (GraphicsDevice.Features[texture.ViewFormat].FormatSupport & FormatSupport.TypedUnorderedAccessView) == 0)
            throw new NotSupportedException("This WebGPU texture format cannot be cleared as a UAV.");
        if (texture.Dimension == TextureDimension.Texture3D && (texture.ArraySlice != 0 || texture.ViewType == ViewType.ArrayBand))
            throw new NotSupportedException("Partial-depth WebGPU texture views are not implemented.");
        var pattern = WebGpuBufferClear.GetTypedPattern(texture.ViewFormat, bits, floats, out var period);
        var range = texture.GetViewSubResourceRange();
        var limits = GraphicsDevice.WebGpuContext.Limits;
        var maximumBytes = Math.Min((ulong)int.MaxValue & ~255UL, Math.Min(limits.maxBufferSize, limits.maxStorageBufferBindingSize));
        for (var mip = range.MipStart; mip < range.MipStart + range.MipCount; mip++)
        {
            var width = Texture.CalculateMipSize(texture.Width, mip);
            var height = Texture.CalculateMipSize(texture.Height, mip);
            var pitch = checked((width * texture.Format.SizeInBytes + 255) & ~255);
            var rows = (int)Math.Min((ulong)height, maximumBytes / (uint)pitch);
            if (rows == 0) throw new NotSupportedException("The texture clear row exceeds WebGPU buffer limits.");
            // Fill at most one plane (or a band of rows), then reuse its contents
            // for all layers/depth slices. All supported element sizes divide
            // the 256-byte row alignment, so padding preserves the pixel pattern.
            using var fill = Buffer.Raw.New(GraphicsDevice, checked(pitch * rows), BufferFlags.UnorderedAccess);
            if (pattern == default)
                Api.wgpuCommandEncoderClearBuffer(encoder, fill.NativeBuffer, 0, (ulong)fill.SizeInBytes);
            else GraphicsDevice.BufferClear.Encode(encoder, fill, pattern, period);
            var source = WGPUTexelCopyBufferInfo.CreateDefault();
            source.buffer = fill.NativeBuffer;
            source.layout.bytesPerRow = (uint)pitch; source.layout.rowsPerImage = (uint)rows;
            var destination = WGPUTexelCopyTextureInfo.CreateDefault();
            destination.texture = texture.NativeTexture; destination.mipLevel = (uint)mip;
            var firstLayer = texture.Dimension == TextureDimension.Texture3D ? 0 : range.ArraySliceStart;
            var layerCount = texture.Dimension == TextureDimension.Texture3D ? Texture.CalculateMipSize(texture.Depth, mip) : range.ArraySliceCount;
            for (var layer = firstLayer; layer < firstLayer + layerCount; layer++)
            for (var y = 0; y < height; y += rows)
            {
                destination.origin.y = (uint)y; destination.origin.z = (uint)layer;
                var extent = new WGPUExtent3D { width = (uint)width, height = (uint)Math.Min(rows, height - y), depthOrArrayLayers = 1 };
                Api.wgpuCommandEncoderCopyBufferToTexture(encoder, &source, &destination, &extent);
            }
            GraphicsDevice.WebGpuContext.ThrowIfFailed();
        }
    }
}
#endif
