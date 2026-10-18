using System;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Net.Http;
using System.Linq;
using System.Runtime.InteropServices;
using Stride.Graphics;
using Stride.Graphics.WebGPU;
using Stride.Core.Mathematics;
using Stride.Rendering;
using Stride.Shaders;
using Api = Stride.Graphics.WebGPU.WebGPU;

internal static class BrowserContextCheck
{
    public static async Task RunAsync(string effectBaseUrl)
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            using var unexpected = await WebGpuDeviceContext.CreatePortableAsync(cancelled.Token);
            throw new Exception("Cancelled browser device creation succeeded.");
        }
        catch (OperationCanceledException) { }
        using var context = await WebGpuDeviceContext.CreatePortableAsync();
        if (context.Limits.maxTextureDimension2D == 0 || context.HasClampToBorder || context.HasEncoderTimestamps)
            throw new Exception("Portable browser device exposed invalid limits/native capabilities.");
        var buffer = CreateBuffer(context);
        try
        {
            for (var iteration = 0; iteration < 3; iteration++)
            {
                WriteBytes(context, buffer);
                var bytes = await context.ReadBufferAsync(buffer, 16);
                for (var index = 0; index < bytes.Length; index++)
                    if (bytes[index] != index + 1) throw new Exception("Shared browser context returned incorrect GPU bytes.");
                await context.WaitForIdleAsync();
            }
            using var cancellation = new CancellationTokenSource();
            var pending = context.ReadBufferAsync(buffer, 16, cancellation.Token);
            cancellation.Cancel();
            try { await pending; throw new Exception("Cancelled browser map succeeded."); }
            catch (OperationCanceledException) { }
            // Yielding lets the expired native map callback run before buffer reuse.
            await Task.Delay(1);
            var retried = await context.ReadBufferAsync(buffer, 16);
            if (retried[15] != 16) throw new Exception("Browser map retry failed after cancellation.");
            var duringDisposal = context.ReadBufferAsync(buffer, 16);
            context.Dispose();
            try { await duringDisposal; throw new Exception("Disposed browser context completed a pending read."); }
            catch (OperationCanceledException) { }
            context.Dispose();
        }
        finally { ReleaseBuffer(buffer); }
        await VerifyEngineDevice(effectBaseUrl);
    }

    private static async Task VerifyEngineDevice(string effectBaseUrl)
    {
        GraphicsAdapterFactory.Dispose();
        try
        {
            await GraphicsAdapterFactory.InitializeWebGPUAsync();
            // An unused prepared device is owned by the factory.
            GraphicsAdapterFactory.Dispose();
            await GraphicsAdapterFactory.InitializeWebGPUAsync();
            await using var device = GraphicsDevice.New();
            if (device.TimestampFrequency != 0 || GraphicsDevice.Platform != GraphicsPlatform.WebGPU)
                throw new Exception("Browser engine device exposed native timestamps or the wrong graphics platform.");
            using var uploaded = Stride.Graphics.Buffer.New(device, new uint[] { 1, 2, 3, 4 }, BufferFlags.VertexBuffer, GraphicsResourceUsage.Default);
            using var commands = CommandList.New(device);
            using var target = Texture.New2D(device, 17, 9, PixelFormat.R8G8B8A8_UNorm, TextureFlags.RenderTarget);
            using var staging = target.ToStaging();
            var pixels = new uint[17 * 9];
            for (var frame = 0; frame < 3; frame++)
            {
                var expected = frame == 1 ? 0xff00ff00u : 0xff0000ffu;
                commands.Clear(target, frame == 1 ? new Color4(0, 1, 0, 1) : new Color4(1, 0, 0, 1));
                commands.Copy(target, staging);
                var started = Stopwatch.GetTimestamp();
                while (!staging.GetData(commands, pixels, doNotWait: true))
                {
                    if (Stopwatch.GetElapsedTime(started).TotalSeconds > 10)
                        throw new TimeoutException("Stride browser texture readback did not complete.");
                    await Task.Delay(1);
                }
                foreach (var pixel in pixels)
                    if (pixel != expected) throw new Exception("Stride browser command-list clear returned incorrect pixels.");
            }
            await device.WaitForWebGPUIdleAsync();
            await VerifyEffects(device, commands, effectBaseUrl);
        }
        finally { GraphicsAdapterFactory.Dispose(); }
    }

    private static async Task VerifyEffects(GraphicsDevice device, CommandList commands, string effectBaseUrl)
    {
        using var http = new HttpClient { BaseAddress = new Uri(effectBaseUrl), Timeout = TimeSpan.FromSeconds(10) };
        var textureCode = EffectBytecode.FromBytes(await http.GetByteArrayAsync("WebGpuValidation.effect"));
        var computeCode = EffectBytecode.FromBytes(await http.GetByteArrayAsync("WebGpuCompute.effect"));
        if (textureCode.Stages.Concat(computeCode.Stages).Any(stage => !WebGpuShaderPackage.IsPackage(stage.Data)))
            throw new Exception("Browser effect contains raw SPIR-V instead of packaged WGSL.");
        using var effect = new Effect(device, textureCode);
        using var instance = new EffectInstance(effect);
        instance.Parameters.Set(SpriteBaseKeys.MatrixTransform, Matrix.Identity);
        using var pattern = Texture.New2D(device, 2, 2, PixelFormat.R8G8B8A8_UNorm,
            new uint[] { 0xffffffff, 0xff0000ff, 0xff00ff00, 0xffff0000 });
        instance.Parameters.Set(TexturingKeys.Texture0, pattern);
        instance.Parameters.Set(TexturingKeys.Sampler, device.SamplerStates.PointClamp);
        using var output = Texture.New2D(device, 2, 2, PixelFormat.R8G8B8A8_UNorm, TextureFlags.RenderTarget);
        using var staging = output.ToStaging();
        commands.SetRenderTargetAndViewport(null, output);
        var graphics = new GraphicsContext(device, commandList: commands);
        graphics.DrawQuad(instance);
        commands.Copy(output, staging);
        var pixels = new uint[4];
        var started = Stopwatch.GetTimestamp();
        while (!staging.GetData(commands, pixels, doNotWait: true))
        {
            if (Stopwatch.GetElapsedTime(started).TotalSeconds > 10) throw new TimeoutException("WGSL textured readback timed out.");
            await Task.Delay(1);
        }
        if (!pixels.SequenceEqual(new uint[] { 0xffbf8040, 0xff000040, 0xff008000, 0xffbf0000 }))
            throw new Exception("Browser SDSL/WGSL texture pixels or orientation changed.");
        using var computeEffect = new Effect(device, computeCode);
        using var compute = new EffectInstance(computeEffect);
        using var values = Stride.Graphics.Buffer.Structured.New(device, 64, sizeof(uint), unorderedAccess: true);
        compute.Parameters.Set(ParameterKeys.NewObject<Stride.Graphics.Buffer>(name: "WebGpuCompute.Values"), values);
        compute.UpdateEffect(device);
        var pipeline = new MutablePipelineState(device);
        pipeline.State.RootSignature = compute.RootSignature;
        pipeline.State.EffectBytecode = computeCode;
        pipeline.Update();
        commands.SetPipelineState(pipeline.CurrentState!);
        compute.Apply(graphics);
        commands.Dispatch(8, 1, 1);
        using var readback = values.ToStaging();
        commands.Copy(values, readback);
        var data = new int[64];
        started = Stopwatch.GetTimestamp();
        MappedResource mapped;
        while ((mapped = commands.MapSubResource(readback, 0, MapMode.Read, doNotWait: true)).DataBox.IsEmpty)
        {
            if (Stopwatch.GetElapsedTime(started).TotalSeconds > 10) throw new TimeoutException("WGSL compute readback timed out.");
            await Task.Delay(1);
        }
        try { Marshal.Copy(mapped.DataBox.DataPointer, data, 0, data.Length); }
        finally { commands.UnmapSubResource(mapped); }
        if (!data.SequenceEqual(Enumerable.Range(0, 64).Select(index => index * 3 + 7)))
            throw new Exception("Browser SDSL/WGSL compute returned incorrect values.");
    }

    private static unsafe WGPUBuffer CreateBuffer(WebGpuDeviceContext context)
    {
        var descriptor = WGPUBufferDescriptor.CreateDefault();
        descriptor.size = 16;
        descriptor.usage = WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst;
        return Api.wgpuDeviceCreateBuffer(context.Device, &descriptor);
    }
    private static unsafe void WriteBytes(WebGpuDeviceContext context, WGPUBuffer buffer)
    {
        var bytes = stackalloc byte[16];
        for (var i = 0; i < 16; i++) bytes[i] = (byte)(i + 1);
        Api.wgpuQueueWriteBuffer(context.Queue, buffer, 0, bytes, 16);
        Api.wgpuQueueSubmit(context.Queue, 0, null);
    }
    private static unsafe void ReleaseBuffer(WGPUBuffer buffer) => Api.wgpuBufferRelease(buffer);
}
