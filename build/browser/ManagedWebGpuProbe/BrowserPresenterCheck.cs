using System;
using System.Diagnostics;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Stride.Core.Mathematics;
using Stride.Games;
using Stride.Graphics;

[SupportedOSPlatform("browser")]
internal static partial class BrowserPresenterCheck
{
    public static async Task RunAsync(GraphicsDevice device)
    {
        device.ColorSpace = ColorSpace.Gamma;
        using var gamma = Create(device, "#engine-gamma");
        await Exercise(device, gamma, "#engine-gamma", false);
        device.ColorSpace = ColorSpace.Linear;
        using var linear = Create(device, "#engine-linear");
        await Exercise(device, linear, "#engine-linear", true);
        Ready();
        await WaitForScreenshots();
    }

    private static SwapChainGraphicsPresenter Create(GraphicsDevice device, string selector)
        => new(device, new PresentationParameters
        {
            BackBufferWidth = 65, BackBufferHeight = 63,
            BackBufferFormat = PixelFormat.R8G8B8A8_UNorm,
            DepthStencilFormat = PixelFormat.None,
            DeviceWindowHandle = new WindowHandle(AppContextType.Browser, selector, IntPtr.Zero),
            PresentationInterval = PresentInterval.One,
        });

    private static async Task Exercise(GraphicsDevice device, SwapChainGraphicsPresenter presenter, string selector, bool srgb)
    {
        using var commands = CommandList.New(device);
        using var preserved = presenter.BackBuffer.ToStaging();
        commands.Clear(presenter.BackBuffer, new Color4(.25f, .5f, .75f, 1));
        commands.Copy(presenter.BackBuffer, preserved);
        commands.Flush();
        // Resize with the old texture's GPU copy submitted but not waited on.
        ResizeCanvas(selector, 97, 71);
        presenter.Resize(97, 71, PixelFormat.R8G8B8A8_UNorm);
        var oldPixels = new uint[65 * 63];
        var started = Stopwatch.GetTimestamp();
        while (!preserved.GetData(commands, oldPixels, doNotWait: true))
        {
            if (Stopwatch.GetElapsedTime(started).TotalSeconds > 10) throw new TimeoutException("Presenter resize readback timed out.");
            await Task.Delay(1);
        }
        var blueFirst = preserved.ViewFormat is PixelFormat.B8G8R8A8_UNorm or PixelFormat.B8G8R8A8_UNorm_SRgb;
        foreach (var pixel in oldPixels)
        {
            var r = (int)((pixel >> (blueFirst ? 16 : 0)) & 255);
            var g = (int)((pixel >> 8) & 255);
            var b = (int)((pixel >> (blueFirst ? 0 : 16)) & 255);
            if (Math.Abs(r - (srgb ? 137 : 64)) > 1 || Math.Abs(g - (srgb ? 188 : 128)) > 1 || Math.Abs(b - (srgb ? 225 : 191)) > 1 || (pixel >> 24) != 255)
                throw new Exception("Resizing discarded submitted pixels or changed color encoding.");
        }
        presenter.Resize(0, 0, PixelFormat.R8G8B8A8_UNorm);
        presenter.Present(); // Zero size must skip acquisition.
        ResizeCanvas(selector, 65, 63);
        presenter.Resize(65, 63, PixelFormat.R8G8B8A8_UNorm);
        for (var frame = 0; frame < 3; frame++)
        {
            commands.Reset();
            commands.Clear(presenter.BackBuffer, new Color4(.25f, .5f, .75f, 1));
            commands.Flush();
            presenter.Present();
            await NextFrame();
        }
        var expectedPixels = (uint[])oldPixels.Clone();
        commands.Reset();
        commands.Copy(presenter.BackBuffer, preserved);
        started = Stopwatch.GetTimestamp();
        while (!preserved.GetData(commands, oldPixels, doNotWait: true))
        {
            if (Stopwatch.GetElapsedTime(started).TotalSeconds > 10) throw new TimeoutException("Final presenter backbuffer readback timed out.");
            await Task.Delay(1);
        }
        if (!oldPixels.AsSpan().SequenceEqual(expectedPixels)) throw new Exception("Final presenter backbuffer pixels changed after resize.");
        await NextFrame();
    }

    [JSImport("resizeCanvas", "managed-gpu-probe")] private static partial void ResizeCanvas(string selector, int width, int height);
    [JSImport("canvasReady", "managed-gpu-probe")] private static partial void Ready();
    [JSImport("waitForCanvasChecks", "managed-gpu-probe")] private static partial Task WaitForScreenshots();
    [JSImport("nextFrame", "managed-gpu-probe")] private static partial Task NextFrame();
}
