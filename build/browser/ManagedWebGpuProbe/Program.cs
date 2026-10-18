using System;
using System.Diagnostics;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading.Tasks;

[SupportedOSPlatform("browser")]
public static partial class Program
{
    public static async Task Main()
    {
        try { await RunAsync(); }
        catch (Exception error) { ReportManagedError(error.ToString()); throw; }
    }

    private static async Task RunAsync()
    {
        for (int iteration = 0; iteration < 3; iteration++)
        {
            var started = Stopwatch.GetTimestamp();
            using (var frame = await ManagedFrame.CreateAsync(NoAdapter()))
            {
                var pixels = await frame.ReadPixelsAsync(iteration == 0);
                var counts = TrianglePixels.Verify(pixels.Length, 65, 63, index => pixels[index]);
                ReportFrame(iteration, counts.Foreground, counts.Background, frame.InvalidMapRejected,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                await NextFrame();
            }
            if (ManagedFrame.LiveFrames != 0 || ManagedFrame.PendingCallbacks != 0)
                throw new InvalidOperationException("Managed frame/callback ownership leaked.");
        }
        // The shared loader must reject a native DLL request explicitly in browser.
        bool rejected = false;
        try { Stride.Graphics.WebGPU.WebGPU.LoadNativeLibrary("unused.dll"); }
        catch (PlatformNotSupportedException) { rejected = true; }
        if (!rejected) throw new InvalidOperationException("Native loader unexpectedly accepted browser use.");
        await BrowserAssetCheck.RunAsync(ContentBaseUrl());
        ReportAssets();
        await BrowserInputCheck.RunAsync();
        await BrowserContextCheck.RunAsync(EffectBaseUrl());
        await BrowserGameLoopCheck.RunAsync();
        ReportGameLoop();
        ReportContext();
        ReportEngineDevice(3, 17 * 9 * 3);
        ReportEffects(4, 64);
        ReportSuccess();
    }

    [JSImport("noAdapter", "managed-gpu-probe")] private static partial bool NoAdapter();
    [JSImport("nextFrame", "managed-gpu-probe")] private static partial Task NextFrame();
    [JSImport("reportFrame", "managed-gpu-probe")]
    private static partial void ReportFrame(int iteration, int foreground, int background, bool invalidMap, double milliseconds);
    [JSImport("reportSuccess", "managed-gpu-probe")] private static partial void ReportSuccess();
    [JSImport("reportContext", "managed-gpu-probe")] private static partial void ReportContext();
    [JSImport("reportEngineDevice", "managed-gpu-probe")] private static partial void ReportEngineDevice(int clearFrames, int verifiedPixels);
    [JSImport("effectBaseUrl", "managed-gpu-probe")] private static partial string EffectBaseUrl();
    [JSImport("reportEffects", "managed-gpu-probe")] private static partial void ReportEffects(int pixels, int values);
    [JSImport("reportGameLoop", "managed-gpu-probe")] private static partial void ReportGameLoop();
    [JSImport("contentBaseUrl", "managed-gpu-probe")] private static partial string ContentBaseUrl();
    [JSImport("reportAssets", "managed-gpu-probe")] private static partial void ReportAssets();
    [JSImport("reportManagedError", "managed-gpu-probe")] private static partial void ReportManagedError(string error);
}
