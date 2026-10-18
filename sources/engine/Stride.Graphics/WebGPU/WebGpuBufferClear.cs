// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_GRAPHICS_API_WEBGPU
using System;
using Stride.Core.Mathematics;
using Stride.Graphics.WebGPU;
using Api = Stride.Graphics.WebGPU.WebGPU;
namespace Stride.Graphics;

internal sealed unsafe class WebGpuBufferClear : IDisposable
{
    private readonly WebGpuDeviceContext context;
    private WGPUComputePipeline pipeline;
    private WGPUBindGroupLayout layout;
    internal WebGpuBufferClear(WebGpuDeviceContext context)
    {
        this.context = context;
        ReadOnlySpan<byte> source = """
            struct Params { bits: vec4<u32>, count: u32, period: u32, width: u32, padding: u32 };
            @group(0) @binding(0) var<storage, read_write> output: array<u32>;
            @group(0) @binding(1) var<uniform> params: Params;
            @compute @workgroup_size(64)
            fn main(@builtin(global_invocation_id) id: vec3<u32>) {
                let i = id.x + id.y * params.width;
                if (i < params.count) { output[i] = params.bits[i % params.period]; }
            }
            """u8;
        WGPUShaderModule module = default;
        try
        {
            fixed (byte* text = source)
            {
                var wgsl = WGPUShaderSourceWGSL.CreateDefault();
                wgsl.code = new WGPUStringView { data = text, length = (nuint)source.Length };
                var shader = WGPUShaderModuleDescriptor.CreateDefault(); shader.nextInChain = &wgsl.chain;
                module = Api.wgpuDeviceCreateShaderModule(context.Device, &shader);
            }
            var descriptor = WGPUComputePipelineDescriptor.CreateDefault(); descriptor.compute.module = module;
            pipeline = Api.wgpuDeviceCreateComputePipeline(context.Device, &descriptor);
            context.ThrowIfFailed();
            layout = Api.wgpuComputePipelineGetBindGroupLayout(pipeline, 0);
            context.ThrowIfFailed();
        }
        catch { Dispose(); throw; }
        finally { if (module.Handle != 0) Api.wgpuShaderModuleRelease(module); }
    }

    internal void Encode(WGPUCommandEncoder encoder, Buffer buffer, UInt4 bits, uint period)
    {
        var size = (ulong)((buffer.SizeInBytes + 3L) & ~3L);
        if (size > context.Limits.maxStorageBufferBindingSize)
            throw new NotSupportedException("A nonzero WebGPU buffer clear currently requires a single storage binding within the device limit.");
        var groups = ((uint)(size / 4) + 63) / 64;
        var columns = Math.Min(groups, context.Limits.maxComputeWorkgroupsPerDimension);
        WGPUBuffer uniform = default;
        WGPUBindGroup group = default;
        WGPUComputePassEncoder pass = default;
        try
        {
            var descriptor = WGPUBufferDescriptor.CreateDefault();
            descriptor.size = 32; descriptor.usage = WGPUBufferUsage.Uniform; descriptor.mappedAtCreation = 1;
            uniform = Api.wgpuDeviceCreateBuffer(context.Device, &descriptor);
            context.ThrowIfFailed();
            var data = (uint*)Api.wgpuBufferGetMappedRange(uniform, 0, 32);
            if (data == null) throw new InvalidOperationException("Clear parameters returned a null mapping.");
            data[0] = bits.X; data[1] = bits.Y; data[2] = bits.Z; data[3] = bits.W;
            data[4] = (uint)(size / 4); data[5] = period; data[6] = columns * 64; data[7] = 0;
            Api.wgpuBufferUnmap(uniform);
            var entries = stackalloc WGPUBindGroupEntry[2];
            entries[0] = WGPUBindGroupEntry.CreateDefault(); entries[0].buffer = buffer.NativeBuffer; entries[0].size = size;
            entries[1] = WGPUBindGroupEntry.CreateDefault(); entries[1].binding = 1; entries[1].buffer = uniform; entries[1].size = 32;
            var bindings = WGPUBindGroupDescriptor.CreateDefault(); bindings.layout = layout; bindings.entryCount = 2; bindings.entries = entries;
            group = Api.wgpuDeviceCreateBindGroup(context.Device, &bindings);
            context.ThrowIfFailed();
            var compute = WGPUComputePassDescriptor.CreateDefault(); pass = Api.wgpuCommandEncoderBeginComputePass(encoder, &compute);
            Api.wgpuComputePassEncoderSetPipeline(pass, pipeline);
            Api.wgpuComputePassEncoderSetBindGroup(pass, 0, group, 0, null);
            Api.wgpuComputePassEncoderDispatchWorkgroups(pass, columns, (groups + columns - 1) / columns, 1);
            context.ThrowIfFailed();
        }
        finally
        {
            if (pass.Handle != 0) { Api.wgpuComputePassEncoderEnd(pass); Api.wgpuComputePassEncoderRelease(pass); }
            if (group.Handle != 0) Api.wgpuBindGroupRelease(group);
            if (uniform.Handle != 0) Api.wgpuBufferRelease(uniform);
        }
    }

    internal static UInt4 GetPattern(Buffer buffer, UInt4 bits, Vector4? floats, out uint period)
    {
        if ((buffer.Flags & BufferFlags.UnorderedAccess) == 0 || buffer.Usage == GraphicsResourceUsage.Staging)
            throw new ArgumentException("Expecting a Buffer supporting UAV.", nameof(buffer));
        if ((buffer.Flags & (BufferFlags.RawBuffer | BufferFlags.StructuredBuffer)) != 0)
        {
            if (floats != null) throw new NotSupportedException("Raw and structured buffers require integer clear values.");
            period = 1; return new UInt4(bits.X, 0, 0, 0);
        }
        return GetTypedPattern(buffer.ViewFormat, bits, floats, out period);
    }

    internal static UInt4 GetTypedPattern(PixelFormat viewFormat, UInt4 bits, Vector4? floats, out uint period)
    {
        var format = GetClearFormat(viewFormat)
            ?? throw new NotSupportedException($"Clearing format {viewFormat} is not supported.");
        if (floats != null && format.Kind is ClearChannelKind.UInt or ClearChannelKind.SInt)
            throw new NotSupportedException("Integer buffer views require integer clear values.");
        Span<byte> element = stackalloc byte[16]; element.Clear();
        for (var channel = 0; channel < format.ChannelCount; channel++)
        {
            var value = floats is Vector4 v ? ConvertChannel(v[channel], format.ChannelBits, format.Kind) : bits[(uint)channel];
            for (var part = 0; part < format.ChannelBits / 8; part++)
                element[channel * (format.ChannelBits / 8) + part] = (byte)(value >> (part * 8));
        }
        var elementSize = format.ChannelCount * format.ChannelBits / 8;
        period = (uint)Math.Max(1, elementSize / 4);
        Span<byte> repeated = stackalloc byte[16]; repeated.Clear();
        for (var i = 0; i < period * 4; i++) repeated[i] = element[i % elementSize];
        return new UInt4(BitConverter.ToUInt32(repeated), BitConverter.ToUInt32(repeated[4..]),
            BitConverter.ToUInt32(repeated[8..]), BitConverter.ToUInt32(repeated[12..]));
    }

    private static uint ConvertChannel(float value, int channelBits, ClearChannelKind kind) => kind switch
    {
        ClearChannelKind.Float when channelBits == 32 => BitConverter.SingleToUInt32Bits(value),
        ClearChannelKind.Float => BitConverter.HalfToUInt16Bits((System.Half)value),
        ClearChannelKind.UNorm => (uint)MathF.Round(Math.Clamp(value, 0, 1) * ((1u << channelBits) - 1)),
        ClearChannelKind.SNorm => (uint)(int)MathF.Round(Math.Clamp(value, -1, 1) * ((1 << (channelBits - 1)) - 1)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private enum ClearChannelKind { Float, UNorm, SNorm, UInt, SInt }
        private static (int ChannelCount, int ChannelBits, ClearChannelKind Kind)? GetClearFormat(PixelFormat format) => format switch
        {
            PixelFormat.R32G32B32A32_Float => (4, 32, ClearChannelKind.Float),
            PixelFormat.R32G32B32A32_UInt => (4, 32, ClearChannelKind.UInt),
            PixelFormat.R32G32B32A32_SInt => (4, 32, ClearChannelKind.SInt),
            PixelFormat.R32G32B32_Float => (3, 32, ClearChannelKind.Float),
            PixelFormat.R32G32B32_UInt => (3, 32, ClearChannelKind.UInt),
            PixelFormat.R32G32B32_SInt => (3, 32, ClearChannelKind.SInt),
            PixelFormat.R32G32_Float => (2, 32, ClearChannelKind.Float),
            PixelFormat.R32G32_UInt => (2, 32, ClearChannelKind.UInt),
            PixelFormat.R32G32_SInt => (2, 32, ClearChannelKind.SInt),
            PixelFormat.R32_Float => (1, 32, ClearChannelKind.Float),
            PixelFormat.R32_UInt => (1, 32, ClearChannelKind.UInt),
            PixelFormat.R32_SInt => (1, 32, ClearChannelKind.SInt),

            PixelFormat.R16G16B16A16_Float => (4, 16, ClearChannelKind.Float),
            PixelFormat.R16G16B16A16_UNorm => (4, 16, ClearChannelKind.UNorm),
            PixelFormat.R16G16B16A16_UInt => (4, 16, ClearChannelKind.UInt),
            PixelFormat.R16G16B16A16_SNorm => (4, 16, ClearChannelKind.SNorm),
            PixelFormat.R16G16B16A16_SInt => (4, 16, ClearChannelKind.SInt),
            PixelFormat.R16G16_Float => (2, 16, ClearChannelKind.Float),
            PixelFormat.R16G16_UNorm => (2, 16, ClearChannelKind.UNorm),
            PixelFormat.R16G16_UInt => (2, 16, ClearChannelKind.UInt),
            PixelFormat.R16G16_SNorm => (2, 16, ClearChannelKind.SNorm),
            PixelFormat.R16G16_SInt => (2, 16, ClearChannelKind.SInt),
            PixelFormat.R16_Float => (1, 16, ClearChannelKind.Float),
            PixelFormat.R16_UNorm => (1, 16, ClearChannelKind.UNorm),
            PixelFormat.R16_UInt => (1, 16, ClearChannelKind.UInt),
            PixelFormat.R16_SNorm => (1, 16, ClearChannelKind.SNorm),
            PixelFormat.R16_SInt => (1, 16, ClearChannelKind.SInt),

            PixelFormat.R8G8B8A8_UNorm => (4, 8, ClearChannelKind.UNorm),
            PixelFormat.R8G8B8A8_UInt => (4, 8, ClearChannelKind.UInt),
            PixelFormat.R8G8B8A8_SNorm => (4, 8, ClearChannelKind.SNorm),
            PixelFormat.R8G8B8A8_SInt => (4, 8, ClearChannelKind.SInt),
            PixelFormat.R8G8_UNorm => (2, 8, ClearChannelKind.UNorm),
            PixelFormat.R8G8_UInt => (2, 8, ClearChannelKind.UInt),
            PixelFormat.R8G8_SNorm => (2, 8, ClearChannelKind.SNorm),
            PixelFormat.R8G8_SInt => (2, 8, ClearChannelKind.SInt),
            PixelFormat.R8_UNorm => (1, 8, ClearChannelKind.UNorm),
            PixelFormat.R8_UInt => (1, 8, ClearChannelKind.UInt),
            PixelFormat.R8_SNorm => (1, 8, ClearChannelKind.SNorm),
            PixelFormat.R8_SInt => (1, 8, ClearChannelKind.SInt),

            _ => null
        };

    public void Dispose()
    {
        if (layout.Handle != 0) Api.wgpuBindGroupLayoutRelease(layout);
        if (pipeline.Handle != 0) Api.wgpuComputePipelineRelease(pipeline);
        layout = default; pipeline = default;
    }
}
#endif
