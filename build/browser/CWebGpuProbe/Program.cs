using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading.Tasks;

[SupportedOSPlatform("browser")]
public static partial class Program
{
    private const int Width = 65, Height = 63;
    private static TaskCompletionSource<(byte[] Pixels, int Stride, bool InvalidMap)>? pending;

    public static async Task Main()
    {
        for (int iteration = 0; iteration < 3; iteration++)
        {
            if (pending is not null) throw new InvalidOperationException("A probe frame is already in flight.");
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = pending.Task;
            var started = Stopwatch.GetTimestamp();
            StartFrame(iteration == 0);
            var frame = await task;
            if (LiveContexts() != 0) throw new InvalidOperationException("Native frame resources leaked.");
            var counts = TrianglePixels.Verify(frame.Pixels.Length, Width, Height, index => frame.Pixels[index]);
            ReportFrame(iteration, counts.Foreground, counts.Background, frame.Stride, frame.Pixels.Length,
                frame.InvalidMap, AdapterType(), Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            // Yield a real browser frame between independent device/surface lifetimes.
            await NextFrame();
        }
        ReportSuccess();
    }

    private static unsafe void StartFrame(bool invalidMap) => Render(Width, Height, invalidMap ? 1 : 0, NoAdapter() ? 1 : 0, &Completed);

    // The native mapped range is valid only during this callback. Copy it before
    // returning; asynchronous continuations run after native cleanup completes.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void Completed(int status, nint data, int length, int stride, int invalidMap)
    {
        var completion = pending;
        pending = null;
        if (completion is null) return;
        try
        {
            if (status != 0)
                throw new InvalidOperationException(status == -101 ? "Injected adapter unavailable" : $"C WebGPU probe failed at stage {status}.");
            if (data == 0 || length != stride * Height || stride < Width * 4)
                throw new InvalidOperationException("Invalid native mapped range.");
            var pixels = new byte[Width * Height * 4];
            for (int row = 0; row < Height; row++)
                Marshal.Copy(data + row * stride, pixels, row * Width * 4, Width * 4);
            completion.TrySetResult((pixels, stride, invalidMap != 0));
        }
        catch (Exception error)
        {
            // Never let a managed exception cross the unmanaged callback boundary.
            completion.TrySetException(error);
        }
    }

    [DllImport("triangle", EntryPoint = "triangle_render")]
    private static extern unsafe void Render(int width, int height, int invalidMap, int noAdapter,
        delegate* unmanaged[Cdecl]<int, nint, int, int, int, void> completed);
    [DllImport("triangle", EntryPoint = "triangle_live_contexts")]
    private static extern int LiveContexts();
    [DllImport("triangle", EntryPoint = "triangle_adapter_type")]
    private static extern int AdapterType();
    [JSImport("noAdapter", "c-gpu-probe")] private static partial bool NoAdapter();
    [JSImport("nextFrame", "c-gpu-probe")] private static partial Task NextFrame();
    [JSImport("reportFrame", "c-gpu-probe")]
    private static partial void ReportFrame(int iteration, int foreground, int background, int stride, int bytes, bool invalidMap, int adapterType, double milliseconds);
    [JSImport("reportSuccess", "c-gpu-probe")] private static partial void ReportSuccess();
}
