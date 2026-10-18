// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_GRAPHICS_API_WEBGPU
using System;
using Stride.Core.ReferenceCounting;
using Stride.Graphics.WebGPU;
using Api = Stride.Graphics.WebGPU.WebGPU;
namespace Stride.Graphics;

public sealed unsafe partial class Texture
{
    private int TexturePixelSize => Format.SizeInBytes;
    private const int TextureSubresourceAlignment = 256;
    private const int TextureRowPitchAlignment = 256;
    internal bool HasStencil;
    internal WGPUTexture NativeTexture;
    internal WGPUTextureView NativeView;
    internal WGPUTextureView NativeDepthShaderView;
    internal bool IsDepthTexture => Format is PixelFormat.D16_UNorm or PixelFormat.D24_UNorm_S8_UInt or PixelFormat.D32_Float;
    internal WGPUBuffer NativeStagingBuffer;
    internal ulong NativeStagingSize;

    public void Recreate(DataBox[] dataBoxes = null) => InitializeFromImpl(dataBoxes);
    public static bool IsDepthStencilReadOnlySupported(GraphicsDevice device) => device.Features.HasDepthAsReadOnlyRT;
    private partial bool IsFlipped() => false;

    internal void RecreateAfterParentResize()
    {
        // InitializeFrom adds a parent reference. Balance the existing view's
        // reference when rebuilding it over the resized parent texture.
        var parent = ParentTexture ?? throw new InvalidOperationException("The texture is not a view.");
        ParentTexture = null;
        parent.ReleaseInternal();
        InitializeFrom(parent, ViewDescription);
    }

    internal override void SwapInternal(GraphicsResourceBase other)
    {
        var texture = (Texture)other;
        base.SwapInternal(other);
        (NativeTexture, texture.NativeTexture) = (texture.NativeTexture, NativeTexture);
        (NativeView, texture.NativeView) = (texture.NativeView, NativeView);
        (NativeDepthShaderView, texture.NativeDepthShaderView) = (texture.NativeDepthShaderView, NativeDepthShaderView);
        (NativeStagingBuffer, texture.NativeStagingBuffer) = (texture.NativeStagingBuffer, NativeStagingBuffer);
        (NativeStagingSize, texture.NativeStagingSize) = (texture.NativeStagingSize, NativeStagingSize);
        (HasStencil, texture.HasStencil) = (texture.HasStencil, HasStencil);
    }

    private partial void InitializeFromImpl(DataBox[] dataBoxes)
    {
        ReleaseNative();
        if (Options != TextureOptions.None) throw new NotSupportedException("WebGPU shared textures are not supported.");
        var nativeFormat = WebGpuConvert.TextureFormat(Format);
        if (Format.IsCompressed && (!GraphicsDevice.WebGpuContext.HasTextureCompressionBC || Dimension is not (TextureDimension.Texture2D or TextureDimension.TextureCube)))
            throw new NotSupportedException("WebGPU BC textures require enabled BC compression and two-dimensional storage.");
        if (Format.IsCompressed && (Width % 4 != 0 || Height % 4 != 0))
            throw new NotSupportedException("WebGPU BC texture base dimensions must be multiples of four texels.");
        HasStencil = Format == PixelFormat.D24_UNorm_S8_UInt;
        try
        {
            if (Usage == GraphicsResourceUsage.Staging)
            {
                if (ParentTexture != null || (IsDepthStencil && Format != PixelFormat.D32_Float) || IsMultiSampled)
                    throw new NotSupportedException("WebGPU staging textures require independent single-sample color or D32 storage.");
                var buffer = WGPUBufferDescriptor.CreateDefault();
                NativeStagingSize = SubresourceLayout(checked(ArraySize * MipLevelCount - 1)).End;
                buffer.size = NativeStagingSize;
                buffer.usage = WGPUBufferUsage.CopyDst | WGPUBufferUsage.MapRead;
                buffer.mappedAtCreation = dataBoxes is { Length: > 0 } ? Api.WGPU_TRUE : Api.WGPU_FALSE;
                NativeStagingBuffer = Api.wgpuDeviceCreateBuffer(GraphicsDevice.WebGpuContext.Device, &buffer);
                GraphicsDevice.WebGpuContext.ThrowIfFailed();
                if (dataBoxes is { Length: > 0 })
                {
                    if (dataBoxes.Length != ArraySize * MipLevelCount) throw new ArgumentException("Initial data must contain every texture subresource.", nameof(dataBoxes));
                    var mapped = (byte*)Api.wgpuBufferGetMappedRange(NativeStagingBuffer, 0, (nuint)NativeStagingSize);
                    if (mapped == null) throw new InvalidOperationException("WebGPU staging initial data could not be mapped.");
                    try
                    {
                        for (var i = 0; i < dataBoxes.Length; i++)
                        {
                            var layout = SubresourceLayout(i);
                            var source = dataBoxes[i];
                            Image.ComputePitch(Format, CalculateMipSize(Width, i % MipLevelCount), CalculateMipSize(Height, i % MipLevelCount), out var rowSize, out _, out _, out _);
                            if (source.DataPointer == IntPtr.Zero || source.RowPitch < rowSize || source.SlicePitch < (long)source.RowPitch * layout.Rows)
                                throw new ArgumentException("Texture source data has an invalid pointer or pitch.", nameof(dataBoxes));
                            for (var z = 0; z < layout.Depth; z++)
                                for (var y = 0; y < layout.Rows; y++)
                                    System.Buffer.MemoryCopy((byte*)source.DataPointer + z * source.SlicePitch + y * source.RowPitch,
                                        mapped + layout.Offset + (z * layout.Rows + y) * layout.RowPitch, rowSize, rowSize);
                        }
                    }
                    finally { Api.wgpuBufferUnmap(NativeStagingBuffer); }
                }
            }
            else
            {
                if (ParentTexture != null)
                {
                    if (dataBoxes is { Length: > 0 }) throw new ArgumentException("Texture views cannot have initial data.", nameof(dataBoxes));
                    NativeTexture = ParentTexture.NativeTexture;
                    Api.wgpuTextureAddRef(NativeTexture);
                }
                else
                {
                    var desc = WGPUTextureDescriptor.CreateDefault();
                    desc.dimension = Dimension switch
                    {
                        TextureDimension.Texture1D => WGPUTextureDimension._2D,
                        TextureDimension.Texture2D or TextureDimension.TextureCube => WGPUTextureDimension._2D,
                        TextureDimension.Texture3D => WGPUTextureDimension._3D,
                        _ => throw new NotSupportedException("Unknown WebGPU texture dimension.")
                    };
                    desc.size = new WGPUExtent3D { width = (uint)Width, height = (uint)Height, depthOrArrayLayers = (uint)(Dimension == TextureDimension.Texture3D ? Depth : ArraySize) };
                    desc.format = nativeFormat;
                    desc.mipLevelCount = (uint)MipLevelCount;
                    // Some Stride 1D descriptions leave the enum at zero.
                    // WebGPU requires an explicit single sample for these.
                    desc.sampleCount = Math.Max(1u, (uint)MultisampleCount);
                    desc.usage = IsMultiSampled ? WGPUTextureUsage.None : WGPUTextureUsage.CopySrc | WGPUTextureUsage.CopyDst;
                    if ((Flags & TextureFlags.ShaderResource) != 0) desc.usage |= WGPUTextureUsage.TextureBinding;
                    // WebGPU 1D textures have one mip and cannot be attachments.
                    // Use height-one 2D storage; shader preparation expands 1D coordinates.
                    if ((Flags & (TextureFlags.RenderTarget | TextureFlags.DepthStencil)) != 0 ||
                        (Dimension is TextureDimension.Texture2D or TextureDimension.TextureCube && WebGpuFormatFeatures.CanResolve(nativeFormat) && !Format.IsCompressed))
                        desc.usage |= WGPUTextureUsage.RenderAttachment;
                    if ((Flags & TextureFlags.UnorderedAccess) != 0 && (GraphicsDevice.Features[Format].FormatSupport & FormatSupport.TypedUnorderedAccessView) == 0)
                        throw new NotSupportedException($"WebGPU storage texture format {Format} is not enabled on this device.");
                    if ((Flags & TextureFlags.UnorderedAccess) != 0) desc.usage |= WGPUTextureUsage.StorageBinding;
                    NativeTexture = Api.wgpuDeviceCreateTexture(GraphicsDevice.WebGpuContext.Device, &desc);
                    GraphicsDevice.WebGpuContext.ThrowIfFailed();
                }
                if (ViewFormat != Format) throw new NotSupportedException("WebGPU texture view format reinterpretation is not implemented.");
                var range = GetViewSubResourceRange();
                var view = WGPUTextureViewDescriptor.CreateDefault();
                view.format = nativeFormat;
                view.baseMipLevel = (uint)range.MipStart;
                view.mipLevelCount = (uint)range.MipCount;
                view.baseArrayLayer = (uint)range.ArraySliceStart;
                view.arrayLayerCount = (uint)range.ArraySliceCount;
                view.dimension = ViewDimension switch
                {
                    TextureDimension.Texture1D => WGPUTextureViewDimension._2D,
                    TextureDimension.Texture3D => WGPUTextureViewDimension._3D,
                    TextureDimension.TextureCube => range.ArraySliceCount == 6 ? WGPUTextureViewDimension.Cube : WGPUTextureViewDimension.CubeArray,
                    _ => range.ArraySliceCount == 1 ? WGPUTextureViewDimension._2D : WGPUTextureViewDimension._2DArray
                };
                NativeView = Api.wgpuTextureCreateView(NativeTexture, &view);
                GraphicsDevice.WebGpuContext.ThrowIfFailed();
                if (IsDepthTexture && (ViewFlags & TextureFlags.ShaderResource) != 0)
                {
                    view.aspect = WGPUTextureAspect.DepthOnly;
                    view.format = WGPUTextureFormat.Undefined; // choose the aspect-specific depth format, including D24S8
                    NativeDepthShaderView = Api.wgpuTextureCreateView(NativeTexture, &view);
                    GraphicsDevice.WebGpuContext.ThrowIfFailed();
                }
                if (dataBoxes is { Length: > 0 })
                {
                    if (dataBoxes.Length != ArraySize * MipLevelCount) throw new ArgumentException("Initial data must contain every texture subresource.", nameof(dataBoxes));
                    for (var i = 0; i < dataBoxes.Length; i++) Upload(i, dataBoxes[i]);
                }
            }
            GraphicsDevice.WebGpuContext.ThrowIfFailed();
        }
        catch { ReleaseNative(); throw; }
    }

    internal (ulong Offset, uint RowPitch, uint Rows, uint Depth, ulong End) SubresourceLayout(int subresource)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(subresource);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(subresource, ArraySize * MipLevelCount);
        ulong offset = 0;
        for (var i = 0; ; i++)
        {
            var mip = i % MipLevelCount;
            Image.ComputePitch(Format, CalculateMipSize(Width, mip), CalculateMipSize(Height, mip), out var packedPitch, out var packedSlice, out _, out _);
            var rowPitch = (uint)((packedPitch + 255) & ~255);
            var rows = (uint)(packedSlice / packedPitch);
            var depth = (uint)CalculateMipSize(Depth, mip);
            var end = checked(offset + rowPitch * (ulong)rows * depth);
            if (i == subresource) return (offset, rowPitch, rows, depth, end);
            offset = end;
        }
    }

    internal void Upload(int subresource, DataBox source)
    {
        if (Usage == GraphicsResourceUsage.Staging || IsDepthStencil || IsMultiSampled) throw new NotSupportedException("WebGPU upload requires a single-sample color texture.");
        var copy = CopyLocation(subresource);
        var extent = CopyExtent(subresource);
        Image.ComputePitch(Format, CalculateMipSize(Width, subresource % MipLevelCount), CalculateMipSize(Height, subresource % MipLevelCount), out var rowSize, out var sliceSize, out _, out _);
        var rows = sliceSize / rowSize;
        if (source.DataPointer == IntPtr.Zero || source.RowPitch < rowSize || source.SlicePitch < source.RowPitch * rows || source.SlicePitch % source.RowPitch != 0)
            throw new ArgumentException("Texture source data has an invalid pointer or pitch.", nameof(source));
        var layout = WGPUTexelCopyBufferLayout.CreateDefault();
        layout.bytesPerRow = (uint)source.RowPitch;
        layout.rowsPerImage = (uint)(source.SlicePitch / source.RowPitch);
        var size = checked((nuint)((extent.depthOrArrayLayers - 1) * source.SlicePitch + (rows - 1) * source.RowPitch + rowSize));
        Api.wgpuQueueWriteTexture(GraphicsDevice.WebGpuContext.Queue, &copy, (void*)source.DataPointer, size, &layout, &extent);
        GraphicsDevice.WebGpuContext.ThrowIfFailed();
    }

    internal WGPUTexelCopyTextureInfo CopyLocation(int subresource)
    {
        _ = SubresourceLayout(subresource);
        var copy = WGPUTexelCopyTextureInfo.CreateDefault();
        copy.texture = NativeTexture;
        copy.mipLevel = (uint)(subresource % MipLevelCount);
        copy.origin.z = Dimension == TextureDimension.Texture3D ? 0 : (uint)(subresource / MipLevelCount);
        copy.aspect = WGPUTextureAspect.All;
        return copy;
    }

    internal WGPUExtent3D CopyExtent(int subresource) => new()
    {
        width = CopyMipSize(Width, subresource % MipLevelCount),
        height = CopyMipSize(Height, subresource % MipLevelCount),
        depthOrArrayLayers = (uint)CalculateMipSize(Depth, subresource % MipLevelCount)
    };

    private uint CopyMipSize(int size, int mip)
    {
        var value = CalculateMipSize(size, mip);
        return (uint)(Format.IsCompressed ? (value + 3) & ~3 : value);
    }

    protected internal override void OnDestroyed(bool immediately = false)
    {
        ReleaseNative();
        base.OnDestroyed(immediately);
    }

    private void ReleaseNative()
    {
        if (NativeStagingBuffer.Handle != 0) GraphicsDevice.WebGpuContext?.CancelPendingRead(NativeStagingBuffer);
        if (NativeView.Handle != 0) Api.wgpuTextureViewRelease(NativeView);
        if (NativeDepthShaderView.Handle != 0) Api.wgpuTextureViewRelease(NativeDepthShaderView);
        if (NativeTexture.Handle != 0) Api.wgpuTextureRelease(NativeTexture);
        if (NativeStagingBuffer.Handle != 0) Api.wgpuBufferRelease(NativeStagingBuffer);
        NativeView = default; NativeDepthShaderView = default; NativeTexture = default; NativeStagingBuffer = default; NativeStagingSize = 0;
    }

    internal static PixelFormat ComputeShaderResourceFormatFromDepthFormat(PixelFormat format) => format;
}
#endif
