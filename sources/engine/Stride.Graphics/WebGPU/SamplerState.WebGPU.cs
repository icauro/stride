// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

#if STRIDE_GRAPHICS_API_WEBGPU
#nullable enable annotations
using System;
using Stride.Graphics.WebGPU;
using Api = Stride.Graphics.WebGPU.WebGPU;

namespace Stride.Graphics
{
    public partial class SamplerState
    {
        internal WGPUSampler NativeSampler;
        private unsafe SamplerState(GraphicsDevice graphicsDevice, ref readonly SamplerStateDescription samplerStateDescription, string? name = null) : base(graphicsDevice, name)
        {
            Description = samplerStateDescription;
            if (Description.MipMapLevelOfDetailBias != 0) throw new NotSupportedException("WebGPU samplers do not expose mip LOD bias.");
            var descriptor = WGPUSamplerDescriptor.CreateDefault();
            var extras = new WGPUSamplerDescriptorExtras();
            if (Description.AddressU == TextureAddressMode.Border || Description.AddressV == TextureAddressMode.Border || Description.AddressW == TextureAddressMode.Border)
            {
                if (!graphicsDevice.WebGpuContext.HasClampToBorder) throw new NotSupportedException("Border samplers require the adapter's native clamp-to-border feature.");
                var color = Description.BorderColor;
                extras.samplerBorderColor = color.R == 0 && color.G == 0 && color.B == 0 && color.A == 0 ? WGPUSamplerBorderColor.TransparentBlack :
                    color.R == 0 && color.G == 0 && color.B == 0 && color.A == 1 ? WGPUSamplerBorderColor.OpaqueBlack :
                    color.R == 1 && color.G == 1 && color.B == 1 && color.A == 1 ? WGPUSamplerBorderColor.OpaqueWhite :
                    throw new NotSupportedException("Native WebGPU border samplers support transparent black, opaque black and opaque white.");
                extras.chain.sType = (WGPUSType)WGPUNativeSType.WGPUSType_SamplerDescriptorExtras;
                descriptor.nextInChain = &extras.chain;
            }
            descriptor.addressModeU = Address(Description.AddressU);
            descriptor.addressModeV = Address(Description.AddressV);
            descriptor.addressModeW = Address(Description.AddressW);
            var filter = (int)Description.Filter;
            descriptor.minFilter = (filter & 16) != 0 ? WGPUFilterMode.Linear : WGPUFilterMode.Nearest;
            descriptor.magFilter = (filter & 4) != 0 ? WGPUFilterMode.Linear : WGPUFilterMode.Nearest;
            descriptor.mipmapFilter = (filter & 1) != 0 ? WGPUMipmapFilterMode.Linear : WGPUMipmapFilterMode.Nearest;
            descriptor.lodMinClamp = Math.Max(0, Description.MinMipLevel);
            descriptor.lodMaxClamp = Description.MaxMipLevel;
            if ((filter & 128) != 0) descriptor.compare = (WGPUCompareFunction)Description.CompareFunction;
            if ((filter & 64) != 0)
            {
                if (Description.MaxAnisotropy is < 1 or > 16) throw new NotSupportedException("WebGPU anisotropy must be between 1 and 16.");
                descriptor.maxAnisotropy = (ushort)Description.MaxAnisotropy;
            }
            NativeSampler = Api.wgpuDeviceCreateSampler(graphicsDevice.WebGpuContext.Device, &descriptor);
            graphicsDevice.WebGpuContext.ThrowIfFailed();
        }
        private static WGPUAddressMode Address(TextureAddressMode mode) => mode switch
        {
            TextureAddressMode.Wrap => WGPUAddressMode.Repeat,
            TextureAddressMode.Clamp => WGPUAddressMode.ClampToEdge,
            TextureAddressMode.Mirror => WGPUAddressMode.MirrorRepeat,
            TextureAddressMode.Border => (WGPUAddressMode)WGPUNativeAddressMode.ClampToBorder,
            _ => throw new NotSupportedException($"WebGPU sampler address mode {mode} is not supported.")
        };
        protected internal override void OnDestroyed(bool immediately = false)
        {
            if (NativeSampler.Handle != 0) Api.wgpuSamplerRelease(NativeSampler);
            NativeSampler = default;
            base.OnDestroyed(immediately);
        }
    }
}

#endif
