using System;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Stride.Graphics;
using Stride.Graphics.WebGPU;
using Stride.Core.Mathematics;
using Api = Stride.Graphics.WebGPU.WebGPU;

internal static class BrowserContextCheck
{
    public static async Task RunAsync()
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
        await VerifyEngineDevice();
    }

    private static async Task VerifyEngineDevice()
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
        }
        finally { GraphicsAdapterFactory.Dispose(); }
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
