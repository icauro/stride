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
                int foreground = 0, background = 0;
                if (pixels.Length != Width * Height * 4)
                    throw new InvalidOperationException($"Unexpected readback length: {pixels.Length}");

                for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    int offset = (y * Width + x) * 4;
                    bool red = pixels[offset] == 255 && pixels[offset + 1] == 0 && pixels[offset + 2] == 0 && pixels[offset + 3] == 255;
                    bool blue = pixels[offset] == 0 && pixels[offset + 1] == 0 && pixels[offset + 2] == 255 && pixels[offset + 3] == 255;
                    if (!red && !blue)
                        throw new InvalidOperationException($"Unexpected RGBA at ({x}, {y})");
                    if (red) foreground++;
                    else background++;

                    // Compare all pixels safely away from edges with the analytic
                    // triangle, so a clear, rectangle or random image cannot pass.
                    double ndcX = 2.0 * (x + 0.5) / Width - 1.0;
                    double ndcY = 1.0 - 2.0 * (y + 0.5) / Height;
                    double edge = 0.4 - 0.5 * ndcY - Math.Abs(ndcX);
                    if (ndcY > -0.7 && ndcY < 0.7 && edge > 0.08 && !red)
                        throw new InvalidOperationException($"Missing triangle pixel ({x}, {y})");
                    if ((ndcY < -0.85 || ndcY > 0.85 || edge < -0.08) && !blue)
                        throw new InvalidOperationException($"Unexpected foreground ({x}, {y})");
                }
                if (foreground < 500 || background < 500)
                    throw new InvalidOperationException("Expected substantial foreground and background.");
                ReportFrame(iteration, foreground, background, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
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
