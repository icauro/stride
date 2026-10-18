// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_GRAPHICS_API_WEBGPU
using System;
using Stride.Graphics.WebGPU;
namespace Stride.Graphics;

internal static class WebGpuConvert
{
    public static WGPUTextureFormat TextureFormat(PixelFormat format) => TryTextureFormat(format, out var native)
        ? native : throw new NotSupportedException($"WebGPU texture format {format} is not supported.");

    public static bool TryTextureFormat(PixelFormat format, out WGPUTextureFormat native)
    {
        native = format switch
        {
        PixelFormat.R8_UNorm => WGPUTextureFormat.R8Unorm,
        PixelFormat.R8_SNorm => WGPUTextureFormat.R8Snorm,
        PixelFormat.R8_UInt => WGPUTextureFormat.R8Uint,
        PixelFormat.R8_SInt => WGPUTextureFormat.R8Sint,
        PixelFormat.R8G8_UNorm => WGPUTextureFormat.RG8Unorm,
        PixelFormat.R8G8_SNorm => WGPUTextureFormat.RG8Snorm,
        PixelFormat.R8G8_UInt => WGPUTextureFormat.RG8Uint,
        PixelFormat.R8G8_SInt => WGPUTextureFormat.RG8Sint,
        PixelFormat.R8G8B8A8_UNorm => WGPUTextureFormat.RGBA8Unorm,
        PixelFormat.R8G8B8A8_UNorm_SRgb => WGPUTextureFormat.RGBA8UnormSrgb,
        PixelFormat.R8G8B8A8_SNorm => WGPUTextureFormat.RGBA8Snorm,
        PixelFormat.R8G8B8A8_UInt => WGPUTextureFormat.RGBA8Uint,
        PixelFormat.R8G8B8A8_SInt => WGPUTextureFormat.RGBA8Sint,
        PixelFormat.B8G8R8A8_UNorm => WGPUTextureFormat.BGRA8Unorm,
        PixelFormat.B8G8R8A8_UNorm_SRgb => WGPUTextureFormat.BGRA8UnormSrgb,
        PixelFormat.R16_Float => WGPUTextureFormat.R16Float,
        PixelFormat.R16_UInt => WGPUTextureFormat.R16Uint,
        PixelFormat.R16_SInt => WGPUTextureFormat.R16Sint,
        PixelFormat.R16G16_Float => WGPUTextureFormat.RG16Float,
        PixelFormat.R16G16_UInt => WGPUTextureFormat.RG16Uint,
        PixelFormat.R16G16_SInt => WGPUTextureFormat.RG16Sint,
        PixelFormat.R16G16B16A16_Float => WGPUTextureFormat.RGBA16Float,
        PixelFormat.R16G16B16A16_UInt => WGPUTextureFormat.RGBA16Uint,
        PixelFormat.R16G16B16A16_SInt => WGPUTextureFormat.RGBA16Sint,
        PixelFormat.R32_Float => WGPUTextureFormat.R32Float,
        PixelFormat.R32_UInt => WGPUTextureFormat.R32Uint,
        PixelFormat.R32_SInt => WGPUTextureFormat.R32Sint,
        PixelFormat.R32G32_Float => WGPUTextureFormat.RG32Float,
        PixelFormat.R32G32_UInt => WGPUTextureFormat.RG32Uint,
        PixelFormat.R32G32_SInt => WGPUTextureFormat.RG32Sint,
        PixelFormat.R32G32B32A32_Float => WGPUTextureFormat.RGBA32Float,
        PixelFormat.R32G32B32A32_UInt => WGPUTextureFormat.RGBA32Uint,
        PixelFormat.R32G32B32A32_SInt => WGPUTextureFormat.RGBA32Sint,
        PixelFormat.R10G10B10A2_UNorm => WGPUTextureFormat.RGB10A2Unorm,
        PixelFormat.R10G10B10A2_UInt => WGPUTextureFormat.RGB10A2Uint,
        PixelFormat.R11G11B10_Float => WGPUTextureFormat.RG11B10Ufloat,
        PixelFormat.R9G9B9E5_Sharedexp => WGPUTextureFormat.RGB9E5Ufloat,
        PixelFormat.D16_UNorm => WGPUTextureFormat.Depth16Unorm,
        PixelFormat.D32_Float => WGPUTextureFormat.Depth32Float,
        PixelFormat.D24_UNorm_S8_UInt => WGPUTextureFormat.Depth24PlusStencil8,
        PixelFormat.BC1_UNorm => WGPUTextureFormat.BC1RGBAUnorm,
        PixelFormat.BC1_UNorm_SRgb => WGPUTextureFormat.BC1RGBAUnormSrgb,
        PixelFormat.BC2_UNorm => WGPUTextureFormat.BC2RGBAUnorm,
        PixelFormat.BC2_UNorm_SRgb => WGPUTextureFormat.BC2RGBAUnormSrgb,
        PixelFormat.BC3_UNorm => WGPUTextureFormat.BC3RGBAUnorm,
        PixelFormat.BC3_UNorm_SRgb => WGPUTextureFormat.BC3RGBAUnormSrgb,
        PixelFormat.BC4_UNorm => WGPUTextureFormat.BC4RUnorm,
        PixelFormat.BC4_SNorm => WGPUTextureFormat.BC4RSnorm,
        PixelFormat.BC5_UNorm => WGPUTextureFormat.BC5RGUnorm,
        PixelFormat.BC5_SNorm => WGPUTextureFormat.BC5RGSnorm,
        PixelFormat.BC6H_Uf16 => WGPUTextureFormat.BC6HRGBUfloat,
        PixelFormat.BC6H_Sf16 => WGPUTextureFormat.BC6HRGBFloat,
        PixelFormat.BC7_UNorm => WGPUTextureFormat.BC7RGBAUnorm,
        PixelFormat.BC7_UNorm_SRgb => WGPUTextureFormat.BC7RGBAUnormSrgb,
        _ => WGPUTextureFormat.Undefined
        };
        return native != WGPUTextureFormat.Undefined;
    }
}
#endif
