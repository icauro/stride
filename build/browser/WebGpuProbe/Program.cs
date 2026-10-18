using System;
using System.Diagnostics;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading.Tasks;

[SupportedOSPlatform("browser")]
public static partial class Program
{
    private const int Width = 65;
    private const int Height = 63;

    public static async Task Main()
    {
        // Non-aligned width deliberately exercises GPU row padding removal.
        using var context = await CreateContext();
        try
        {
            for (int iteration = 0; iteration < 3; iteration++)
            {
                var started = Stopwatch.GetTimestamp();
                using var readback = await RenderAndRead(context, Width, Height, iteration == 0);
                var pixels = CopyReadback(readback);
                var counts = TrianglePixels.Verify(pixels.Length, Width, Height, index => pixels[index]);
                ReportFrame(iteration, counts.Foreground, counts.Background, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            // JS rejection must propagate into the awaiting managed caller.
            bool rejected = false;
            try { await RejectReadback(context); }
            catch (JSException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("Readback rejection did not reach C#.");
            ReportManagedSuccess();
        }
        finally
        {
            DisposeContext(context);
        }
    }

    [JSImport("createContext", "gpu-probe")]
    private static partial Task<JSObject> CreateContext();

    [JSImport("renderAndRead", "gpu-probe")]
    private static partial Task<JSObject> RenderAndRead(JSObject context, int width, int height, bool checkInvalidMap);

    [JSImport("copyReadback", "gpu-probe")]
    [return: JSMarshalAs<JSType.Array<JSType.Number>>]
    private static partial int[] CopyReadback(JSObject readback);

    [JSImport("rejectReadback", "gpu-probe")]
    private static partial Task RejectReadback(JSObject context);

    [JSImport("reportFrame", "gpu-probe")]
    private static partial void ReportFrame(int iteration, int foreground, int background, double elapsedMilliseconds);

    [JSImport("reportManagedSuccess", "gpu-probe")]
    private static partial void ReportManagedSuccess();

    [JSImport("disposeContext", "gpu-probe")]
    private static partial void DisposeContext(JSObject context);
}
