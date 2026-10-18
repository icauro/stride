using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;
using System.Threading;
using Stride.Core.Diagnostics;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Graphics.Font;
using Stride.Core.Mathematics;

internal static partial class BrowserGameStartupCheck
{
    public static async Task RunAsync(string fontUrl)
    {
        GraphicsAdapterFactory.Dispose();
        await GraphicsAdapterFactory.InitializeWebGPUAsync();
        var game = new Game();
        try
        {
            game.GraphicsDeviceManager.PreferredBackBufferWidth = 65;
            game.GraphicsDeviceManager.PreferredBackBufferHeight = 63;
            game.GraphicsDeviceManager.PreferredDepthStencilFormat = PixelFormat.None;
            var context = new GameContextBrowser("#game-loop", 65, 63);
            game.Run(context);
            using (var http = new HttpClient())
            {
                var path = Path.GetTempFileName();
                try
                {
                    await File.WriteAllBytesAsync(path, await http.GetByteArrayAsync(fontUrl));
                    var fonts = (FontSystem)game.Font;
                    fonts.SynchronousGlyphGeneration = false; // Browser must still produce glyphs without a worker thread.
                    fonts.RuntimeFonts.RegisterFont("Browser Liberation Mono", path);
                    using var font = fonts.LoadRuntimeFont("Browser Liberation Mono", 16);
                    if (!font.IsCharPresent('A') || font.IsCharPresent('\0')) throw new Exception("Browser FreeType charmap lookup failed.");
                    font.PreGenerateGlyphs("Space Escape", new Vector2(16));
                    var measured = font.MeasureString("Hello");
                    if (Math.Abs(measured.X - 50) > .01f || measured.Y is < 16 or > 32)
                        throw new Exception($"Browser FreeType font metrics are incorrect: {measured}.");
                }
                finally { File.Delete(path); }
            }
            for (var frame = 0; frame < 3; frame++)
            {
                await NextFrame();
                context.AdvanceFrame(65, 63, true, true);
            }
            if (game.SceneSystem.SceneInstance?.RootScene.Entities.Count != 1)
                throw new Exception("Full browser Game did not load its namespaced scene.");
            if (!game.IsRunning || game.DrawTime.FrameCount < 3)
                throw new Exception("Full browser Game did not advance its draw frames.");
            var key = new ProfilingKey("Browser game startup validation");
            var reader = Profiler.Subscribe();
            Profiler.Enable(key);
            try
            {
                using (Profiler.Begin(key)) { await Task.Delay(2); }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while ((await reader.ReadAsync(timeout.Token)).Key != key) { }
            }
            finally { Profiler.Disable(key); Profiler.Unsubscribe(reader); }
        }
        catch (Exception startup)
        {
            try { await game.DisposeAsync(); }
            catch (Exception cleanup) { throw new AggregateException("Browser Game startup and cleanup failed.", startup, cleanup); }
            throw;
        }
        await game.DisposeAsync();
        GraphicsAdapterFactory.Dispose();
    }
    [JSImport("nextFrame", "managed-gpu-probe")] private static partial Task NextFrame();
}
