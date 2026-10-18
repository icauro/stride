// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
using Stride.Graphics;
using Stride.Graphics.WebGPU;
using Api = Stride.Graphics.WebGPU.WebGPU;

static unsafe class OffscreenValidation
{
    public static void Run(WebGpuDeviceContext context)
    {
        const int size = 32, rowPitch = 256;
        WGPUTexture texture = default; WGPUTextureView view = default;
        WGPUShaderModule shader = default; WGPURenderPipeline pipeline = default;
        WGPUCommandEncoder encoder = default; WGPUCommandBuffer commands = default;
        WGPURenderPassEncoder pass = default; WGPUBuffer staging = default;
        try
        {
            var textureDescriptor = WGPUTextureDescriptor.CreateDefault();
            textureDescriptor.size = new WGPUExtent3D { width = size, height = size, depthOrArrayLayers = 1 };
            textureDescriptor.dimension = WGPUTextureDimension._2D;
            textureDescriptor.format = WGPUTextureFormat.RGBA8Unorm;
            textureDescriptor.usage = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.CopySrc;
            texture = Api.wgpuDeviceCreateTexture(context.Device, &textureDescriptor);
            view = Api.wgpuTextureCreateView(texture, null);
            ReadOnlySpan<byte> source = """
                @vertex fn vs(@builtin(vertex_index) i: u32) -> @builtin(position) vec4<f32> {
                    let positions = array<vec2<f32>,3>(vec2<f32>(-0.8,-0.8), vec2<f32>(0.8,-0.8), vec2<f32>(0.0,0.8));
                    return vec4<f32>(positions[i],0.0,1.0);
                }
                @fragment fn fs() -> @location(0) vec4<f32> { return vec4<f32>(1.0,0.0,0.0,1.0); }
                """u8;
            fixed (byte* code = source)
            {
                var wgsl = WGPUShaderSourceWGSL.CreateDefault();
                wgsl.code = new WGPUStringView { data = code, length = (nuint)source.Length };
                var shaderDescriptor = WGPUShaderModuleDescriptor.CreateDefault();
                shaderDescriptor.nextInChain = &wgsl.chain;
                shader = Api.wgpuDeviceCreateShaderModule(context.Device, &shaderDescriptor);
            }
            var colorTarget = WGPUColorTargetState.CreateDefault();
            colorTarget.format = textureDescriptor.format;
            var fragment = WGPUFragmentState.CreateDefault();
            fragment.module = shader; fragment.targetCount = 1; fragment.targets = &colorTarget;
            fixed (byte* vs = "vs"u8, fs = "fs"u8)
            {
                var descriptor = WGPURenderPipelineDescriptor.CreateDefault();
                descriptor.vertex.module = shader;
                descriptor.vertex.entryPoint = new WGPUStringView { data = vs, length = 2 };
                descriptor.primitive.topology = WGPUPrimitiveTopology.TriangleList;
                fragment.entryPoint = new WGPUStringView { data = fs, length = 2 };
                descriptor.fragment = &fragment;
                pipeline = Api.wgpuDeviceCreateRenderPipeline(context.Device, &descriptor);
            }
            context.ThrowIfFailed();
            var bufferDescriptor = WGPUBufferDescriptor.CreateDefault();
            bufferDescriptor.size = size * rowPitch;
            bufferDescriptor.usage = WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst;
            staging = Api.wgpuDeviceCreateBuffer(context.Device, &bufferDescriptor);
            encoder = Api.wgpuDeviceCreateCommandEncoder(context.Device, null);
            var attachment = WGPURenderPassColorAttachment.CreateDefault();
            attachment.view = view; attachment.loadOp = WGPULoadOp.Clear; attachment.storeOp = WGPUStoreOp.Store;
            attachment.clearValue = new WGPUColor { b = 1, a = 1 };
            var passDescriptor = WGPURenderPassDescriptor.CreateDefault();
            passDescriptor.colorAttachmentCount = 1; passDescriptor.colorAttachments = &attachment;
            pass = Api.wgpuCommandEncoderBeginRenderPass(encoder, &passDescriptor);
            Api.wgpuRenderPassEncoderSetPipeline(pass, pipeline);
            Api.wgpuRenderPassEncoderDraw(pass, 3, 1, 0, 0);
            Api.wgpuRenderPassEncoderEnd(pass);
            Api.wgpuRenderPassEncoderRelease(pass); pass = default;
            var from = WGPUTexelCopyTextureInfo.CreateDefault(); from.texture = texture;
            var to = WGPUTexelCopyBufferInfo.CreateDefault(); to.buffer = staging;
            to.layout.bytesPerRow = rowPitch; to.layout.rowsPerImage = size;
            var extent = textureDescriptor.size;
            Api.wgpuCommandEncoderCopyTextureToBuffer(encoder, &from, &to, &extent);
            commands = Api.wgpuCommandEncoderFinish(encoder, null);
            Api.wgpuQueueSubmit(context.Queue, 1, &commands);
            context.ThrowIfFailed();
            var pixels = context.ReadBuffer(staging, rowPitch * size);
            CheckPixel(pixels, (size / 2) * rowPitch + (size / 2) * 4, 255, 0, 0);
            CheckPixel(pixels, rowPitch + 4, 0, 0, 255);
            context.WaitForIdle();
            Console.WriteLine($"PASS: {context.BackendType} offscreen triangle: red center, blue clear background, exact RGBA readback.");
        }
        finally
        {
            if (pass.Handle != 0) Api.wgpuRenderPassEncoderRelease(pass);
            if (commands.Handle != 0) Api.wgpuCommandBufferRelease(commands);
            if (encoder.Handle != 0) Api.wgpuCommandEncoderRelease(encoder);
            if (staging.Handle != 0) Api.wgpuBufferRelease(staging);
            if (pipeline.Handle != 0) Api.wgpuRenderPipelineRelease(pipeline);
            if (shader.Handle != 0) Api.wgpuShaderModuleRelease(shader);
            if (view.Handle != 0) Api.wgpuTextureViewRelease(view);
            if (texture.Handle != 0) Api.wgpuTextureRelease(texture);
        }
    }
    private static void CheckPixel(byte[] pixels, int offset, byte r, byte g, byte b)
    {
        if (pixels[offset] != r || pixels[offset + 1] != g || pixels[offset + 2] != b || pixels[offset + 3] != 255)
            throw new InvalidDataException($"Unexpected pixel at {offset}: {Convert.ToHexString(pixels.AsSpan(offset, 4))}");
    }
}
