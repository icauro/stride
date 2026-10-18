// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_GRAPHICS_API_WEBGPU
using System;
using System.Numerics;

namespace Stride.Graphics;

public partial struct GraphicsDeviceFeatures
{
    internal GraphicsDeviceFeatures(GraphicsDevice device)
    {
        this = default;
        mapFeaturesPerFormat = new FeaturesPerFormat[256];
        for (var i = 0; i < mapFeaturesPerFormat.Length; i++)
            mapFeaturesPerFormat[i] = WebGpuFormatFeatures.Get((PixelFormat)i, device.WebGpuContext.HasFloat32Filterable, device.WebGpuContext.HasR8Storage, device.WebGpuContext.HasTextureCompressionBC);
        var limits = device.WebGpuContext.Limits;
        MaximumTexture1DSize = checked((int)Math.Min(limits.maxTextureDimension1D, limits.maxTextureDimension2D));
        MaximumTexture2DSize = MaximumTextureCubeSize = checked((int)limits.maxTextureDimension2D);
        MaximumTexture3DSize = checked((int)limits.maxTextureDimension3D);
        MaximumTexture1DArraySize = 1;
        MaximumTexture2DArraySize = checked((int)limits.maxTextureArrayLayers);
        MaximumMipLevels = BitOperations.Log2(limits.maxTextureDimension2D) + 1;
        ResourceSizeInMegabytes = checked((int)(limits.maxBufferSize / (1024 * 1024)));
        HasComputeShaders = true; HasPixelShaderUnorderedAccess = true; HasIndex32Bits = true; HasSRgb = true;
        HasDepthAsSRV = true; HasDepthAsReadOnlyRT = true; HasMultiSampleDepthAsSRV = true;
        RequestedProfile = device.WebGpuRequestedProfile;
        CurrentProfile = device.WebGpuCurrentProfile;
    }
}
#endif
