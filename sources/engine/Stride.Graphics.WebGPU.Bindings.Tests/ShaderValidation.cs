// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
using Stride.Graphics;
using Stride.Graphics.WebGPU;
using Api = Stride.Graphics.WebGPU.WebGPU;

static unsafe class ShaderValidation
{
    public static void Run(string file)
    {
        var path = Environment.GetEnvironmentVariable("STRIDE_WEBGPU_NATIVE_LIBRARY") ?? throw new InvalidOperationException("STRIDE_WEBGPU_NATIVE_LIBRARY is required.");
        var bytes = WebGpuShaderBytecode.Prepare(File.ReadAllBytes(file));
        if (bytes.Length % 4 != 0 || BitConverter.ToUInt32(bytes) != 0x07230203) throw new InvalidDataException("Not a SPIR-V module.");
        foreach (var backend in new[] { WGPUBackendType.Vulkan, WGPUBackendType.D3D12 })
        {
            using var context = WebGpuDeviceContext.Create(path, backend);
            fixed (byte* data = bytes)
            {
                var spirv = WGPUShaderSourceSPIRV.CreateDefault();
                spirv.code = (uint*)data; spirv.codeSize = (uint)bytes.Length / 4;
                var descriptor = WGPUShaderModuleDescriptor.CreateDefault();
                descriptor.nextInChain = &spirv.chain;
                var shader = Api.wgpuDeviceCreateShaderModule(context.Device, &descriptor);
                try { context.ThrowIfFailed(); if (shader.Handle == 0) throw new InvalidOperationException("Null shader module."); }
                finally { if (shader.Handle != 0) Api.wgpuShaderModuleRelease(shader); }
            }
            Console.WriteLine($"PASS: {backend} validated Stride SPIR-V module: {file}");
        }
    }
}
