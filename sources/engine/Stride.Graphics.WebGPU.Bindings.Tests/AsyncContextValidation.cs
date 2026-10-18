// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
using Stride.Graphics;
using Stride.Graphics.WebGPU;
using Api = Stride.Graphics.WebGPU.WebGPU;

static class AsyncContextValidation
{
    public static async Task Run(string nativeLibrary)
    {
        await CheckOwnership();
        Api.LoadNativeLibrary(Path.GetFullPath(nativeLibrary));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            using var unexpected = await WebGpuDeviceContext.CreatePortableAsync(cancelled.Token);
            throw new InvalidDataException("Pre-cancelled device creation succeeded.");
        }
        catch (OperationCanceledException) { }
        foreach (var backend in new[] { WGPUBackendType.Vulkan, WGPUBackendType.D3D12 })
        {
            using var context = await WebGpuDeviceContext.CreatePortableAsync(backend: backend);
            if (context.Limits.maxTextureDimension2D == 0 || context.HasClampToBorder || context.HasEncoderTimestamps)
                throw new InvalidDataException("Portable device exposed native-only capabilities or missing limits.");
            var buffer = CreateReadback(context);
            try
            {
                for (var iteration = 0; iteration < 3; iteration++)
                {
                    WriteBytes(context, buffer);
                    var actual = await context.ReadBufferAsync(buffer, 16);
                    if (!actual.SequenceEqual(Enumerable.Range(1, 16).Select(i => (byte)i)))
                        throw new InvalidDataException("Asynchronous readback returned incorrect bytes.");
                    await context.WaitForIdleAsync();
                }
                try { await context.ReadBufferAsync(buffer, 16, cancelled.Token); throw new InvalidDataException("Pre-cancelled readback succeeded."); }
                catch (OperationCanceledException) { }
                // Reusing after cancellation must remain possible.
                var retried = await context.ReadBufferAsync(buffer, 16);
                if (retried[15] != 16) throw new InvalidDataException("Readback retry failed.");
            }
            finally { ReleaseBuffer(buffer); }
            Console.WriteLine($"PASS: portable async device/queue/exact readback/reuse/cancellation on {backend}: {context.AdapterName}.");
            context.Dispose();
            try { await context.WaitForIdleAsync(); throw new InvalidDataException("Disposed context accepted async work."); }
            catch (ObjectDisposedException) { }
        }
    }

    private static async Task CheckOwnership()
    {
        var released = 0;
        var request = new WebGpuAsyncRequest<int>(value => released += value);
        using var cancelled = new CancellationTokenSource();
        var waiting = request.WaitAsync(() => { }, cancelled.Token);
        cancelled.Cancel();
        try { await waiting; throw new InvalidDataException("Cancelled callback wait succeeded."); }
        catch (OperationCanceledException) { }
        request.Complete(1, null); // A late callback owns a resource that must be released.
        if (released != 1) throw new InvalidDataException("Late callback leaked its resource.");
        request = new WebGpuAsyncRequest<int>(value => released += value);
        request.Complete(2, null); // Completion races a cancellation before consumption.
        try { await request.WaitAsync(() => { }, cancelled.Token); throw new InvalidDataException("Cancellation race succeeded."); }
        catch (OperationCanceledException) { }
        if (released != 3) throw new InvalidDataException("Cancelled completed request leaked its resource.");
        request = new WebGpuAsyncRequest<int>(value => released += value);
        request.Complete(4, null);
        if (await request.WaitAsync(() => { }, default) != 4 || released != 3)
            throw new InvalidDataException("Successful callback did not transfer ownership.");
        request = new WebGpuAsyncRequest<int>(value => released += value);
        request.Complete(8, "expected failure");
        try { await request.WaitAsync(() => { }, default); throw new InvalidDataException("Failed callback succeeded."); }
        catch (InvalidOperationException error) when (error.Message == "expected failure") { }
        if (released != 11) throw new InvalidDataException("Failed callback leaked its resource.");
        Console.WriteLine("PASS: async callback late completion, cancellation race, successful transfer and failure ownership.");
    }

    private static unsafe WGPUBuffer CreateReadback(WebGpuDeviceContext context)
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
