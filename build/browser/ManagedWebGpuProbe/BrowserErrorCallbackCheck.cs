using System;
using System.Text;
using System.Threading.Tasks;
using Stride.Graphics;
using Stride.Graphics.WebGPU;
using Api = Stride.Graphics.WebGPU.WebGPU;

// Deliberately invalid WGSL exercises the actual Emdawn -> Mono -> shared C#
// callback path, not a managed call to the callback or a fabricated error string.
internal static class BrowserErrorCallbackCheck
{
    public const string Marker = "Stride_callback_ΩЖ";

    public static async Task<string> RunAsync()
    {
        await VerifyAbortedMap();
        using var context = await WebGpuDeviceContext.CreatePortableAsync();
        CreateInvalidShader(context);
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await Task.Delay(10);
            try { context.ThrowIfFailed(); }
            catch (InvalidOperationException error)
            {
                if (!error.Message.StartsWith("WebGPU Validation:", StringComparison.Ordinal) ||
                    !error.Message.Contains(Marker, StringComparison.Ordinal) ||
                    !error.Message.Contains("missing_callback_function", StringComparison.Ordinal))
                    throw new InvalidOperationException("GPU callback message was truncated or corrupted: " + error.Message);
                // The text must be copied before Emdawn frees the callback's native bytes.
                await Task.Delay(10);
                GC.Collect();
                if (!error.Message.Contains(Marker, StringComparison.Ordinal)) throw new Exception("GPU callback text did not survive its native lifetime.");
                DestroyDevice(context);
                for (var lostAttempt = 0; !context.IsLost && lostAttempt < 200; lostAttempt++) await Task.Delay(10);
                if (!context.IsLost || !context.DeviceLostDetails.StartsWith("Destroyed:", StringComparison.Ordinal))
                    throw new Exception("Device destruction did not reach the shared lost-device callback: " + context.DeviceLostDetails);
                if (!context.DeviceLostDetails["Destroyed:".Length..].Contains("destroy", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Device-loss callback message was corrupted: " + context.DeviceLostDetails);
                try { context.ThrowIfFailed(); }
                catch (InvalidOperationException lost) when (lost.Message.StartsWith("WebGPU device lost: Destroyed:", StringComparison.Ordinal))
                { return error.Message + "\n" + lost.Message; }
                throw new Exception("Lost-device state did not propagate to managed callers.");
            }
        }
        throw new TimeoutException("Invalid WGSL did not reach the shared GPU error callback.");
    }

    private static unsafe void CreateInvalidShader(WebGpuDeviceContext context)
    {
        var code = Encoding.UTF8.GetBytes("@compute @workgroup_size(1) fn main() { missing_callback_function(); }");
        var label = Encoding.UTF8.GetBytes(Marker);
        fixed (byte* text = code, name = label)
        {
            var source = WGPUShaderSourceWGSL.CreateDefault();
            source.code = new WGPUStringView { data = text, length = (nuint)code.Length };
            var descriptor = WGPUShaderModuleDescriptor.CreateDefault();
            descriptor.nextInChain = &source.chain;
            descriptor.label = new WGPUStringView { data = name, length = (nuint)label.Length };
            var shader = Api.wgpuDeviceCreateShaderModule(context.Device, &descriptor);
            if (shader.Handle != 0) Api.wgpuShaderModuleRelease(shader);
            // Flush Dawn/Chrome's validation delivery, rather than relying on
            // a later application frame to submit work on this isolated device.
            Api.wgpuQueueSubmit(context.Queue, 0, null);
        }
    }

    private static async Task VerifyAbortedMap()
    {
        using var context = await WebGpuDeviceContext.CreatePortableAsync();
        var buffer = CreateReadBuffer(context);
        try
        {
            var read = context.ReadBufferAsync(buffer, 16);
            DestroyBuffer(buffer);
            try { await read; }
            catch (InvalidOperationException error) when (error.Message.StartsWith("WebGPU mapping failed (Aborted):", StringComparison.Ordinal) &&
                error.Message.Contains("destroy", StringComparison.OrdinalIgnoreCase)) { return; }
            throw new Exception("Destroyed buffer did not report a readable asynchronous map failure.");
        }
        finally { ReleaseBuffer(buffer); }
    }

    private static unsafe WGPUBuffer CreateReadBuffer(WebGpuDeviceContext context)
    {
        var descriptor = WGPUBufferDescriptor.CreateDefault();
        descriptor.size = 16;
        descriptor.usage = WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst;
        return Api.wgpuDeviceCreateBuffer(context.Device, &descriptor);
    }
    private static unsafe void DestroyBuffer(WGPUBuffer buffer) => Api.wgpuBufferDestroy(buffer);
    private static unsafe void ReleaseBuffer(WGPUBuffer buffer) => Api.wgpuBufferRelease(buffer);
    private static unsafe void DestroyDevice(WebGpuDeviceContext context) => Api.wgpuDeviceDestroy(context.Device);
}
