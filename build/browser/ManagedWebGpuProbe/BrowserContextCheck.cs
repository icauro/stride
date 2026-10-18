using System;
using System.Threading;
using System.Threading.Tasks;
using Stride.Graphics;
using Stride.Graphics.WebGPU;
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
