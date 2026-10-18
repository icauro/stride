// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_GRAPHICS_API_WEBGPU
using Stride.Graphics.WebGPU;

namespace Stride.Graphics;

internal static class WebGpuFormatFeatures
{
    // WebGPU's guaranteed formats, plus enabled float32 filtering and verified R8 storage.
    // Reference: wgpu v29.0.1 TextureFormat::guaranteed_format_features,
    // https://github.com/gfx-rs/wgpu/blob/v29.0.1/wgpu-types/src/texture/format.rs
    // Keep this bounded by the formats and operations this backend implements.
    public static GraphicsDeviceFeatures.FeaturesPerFormat Get(PixelFormat format, bool float32Filterable = false, bool r8Storage = false, bool textureCompressionBC = false)
    {
        if (!WebGpuConvert.TryTextureFormat(format, out var native))
            return new(format, MultisampleCount.None, ComputeShaderFormatSupport.None, FormatSupport.None);
        if (format.IsCompressed)
            return new(format, MultisampleCount.None, ComputeShaderFormatSupport.None, textureCompressionBC
                ? FormatSupport.Texture2D | FormatSupport.TextureCube | FormatSupport.Mip | FormatSupport.ShaderLoad | FormatSupport.ShaderSample
                : FormatSupport.None);
        var depth = native is WGPUTextureFormat.Depth16Unorm or WGPUTextureFormat.Depth32Float or WGPUTextureFormat.Depth24PlusStencil8;
        var support = FormatSupport.Texture2D | FormatSupport.Mip;
        var compute = ComputeShaderFormatSupport.None;
        if (depth)
            support |= FormatSupport.DepthStencil | FormatSupport.ShaderLoad | FormatSupport.ShaderSample | FormatSupport.ShaderSampleComparison;
        else
        {
            support |= FormatSupport.Texture1D | FormatSupport.Texture3D | FormatSupport.TextureCube | FormatSupport.ShaderLoad;
            // Filtering 32-bit float textures is optional; it does not imply float32 blending or resolve support.
            if (IsFilterable(native) || (float32Filterable && native is
                WGPUTextureFormat.R32Float or WGPUTextureFormat.RG32Float or WGPUTextureFormat.RGBA32Float))
                support |= FormatSupport.ShaderSample;
            if (IsRenderable(native))
            {
                support |= FormatSupport.RenderTarget;
                if (IsFilterable(native)) support |= FormatSupport.Blendable;
            }
            if (IsStorage(native) || (r8Storage && native == WGPUTextureFormat.R8Unorm))
            {
                support |= FormatSupport.TypedUnorderedAccessView;
                compute = ComputeShaderFormatSupport.TypedLoad | ComputeShaderFormatSupport.TypedStore;
            }
        }
        var samples = IsMultisample(native) ? MultisampleCount.X4 : MultisampleCount.None;
        if (samples != MultisampleCount.None) support |= FormatSupport.MultisampleRendertarget;
        if (CanResolve(native)) support |= FormatSupport.MultisampleResolve;
        return new(format, samples, compute, support);
    }

    private static bool IsRenderable(WGPUTextureFormat format) => format is not (
        WGPUTextureFormat.R8Snorm or WGPUTextureFormat.RG8Snorm or WGPUTextureFormat.RGBA8Snorm or
        WGPUTextureFormat.RG11B10Ufloat or WGPUTextureFormat.RGB9E5Ufloat);

    private static bool IsFilterable(WGPUTextureFormat format) => format is
        WGPUTextureFormat.R8Unorm or WGPUTextureFormat.R8Snorm or WGPUTextureFormat.RG8Unorm or WGPUTextureFormat.RG8Snorm or
        WGPUTextureFormat.RGBA8Unorm or WGPUTextureFormat.RGBA8UnormSrgb or WGPUTextureFormat.RGBA8Snorm or
        WGPUTextureFormat.BGRA8Unorm or WGPUTextureFormat.BGRA8UnormSrgb or WGPUTextureFormat.R16Float or
        WGPUTextureFormat.RG16Float or WGPUTextureFormat.RGBA16Float or WGPUTextureFormat.RGB10A2Unorm or
        WGPUTextureFormat.RG11B10Ufloat or WGPUTextureFormat.RGB9E5Ufloat;

    private static bool IsStorage(WGPUTextureFormat format) => format is
        WGPUTextureFormat.R32Uint or WGPUTextureFormat.R32Sint or WGPUTextureFormat.R32Float or
        WGPUTextureFormat.RG32Uint or WGPUTextureFormat.RG32Sint or WGPUTextureFormat.RG32Float or
        WGPUTextureFormat.RGBA32Uint or WGPUTextureFormat.RGBA32Sint or WGPUTextureFormat.RGBA32Float or
        WGPUTextureFormat.RGBA8Unorm or WGPUTextureFormat.RGBA8Snorm or WGPUTextureFormat.RGBA8Uint or WGPUTextureFormat.RGBA8Sint or
        WGPUTextureFormat.RGBA16Uint or WGPUTextureFormat.RGBA16Sint or WGPUTextureFormat.RGBA16Float;

    private static bool IsMultisample(WGPUTextureFormat format) => IsRenderable(format) && format is not (
        WGPUTextureFormat.R32Uint or WGPUTextureFormat.R32Sint or
        WGPUTextureFormat.RG32Uint or WGPUTextureFormat.RG32Sint or WGPUTextureFormat.RG32Float or
        WGPUTextureFormat.RGBA32Uint or WGPUTextureFormat.RGBA32Sint or WGPUTextureFormat.RGBA32Float);

    internal static bool CanResolve(WGPUTextureFormat format) => IsRenderable(format) && IsFilterable(format);
}
#endif
