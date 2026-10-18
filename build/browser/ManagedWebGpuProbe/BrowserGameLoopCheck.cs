using System;
using System.Diagnostics;
using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;
using Stride.Core.Mathematics;
using Stride.Games;
using Stride.Graphics;

internal static partial class BrowserGameLoopCheck
{
    public static async Task RunAsync()
    {
        for (var cycle = 0; cycle < 2; cycle++)
        {
            GraphicsAdapterFactory.Dispose();
            await GraphicsAdapterFactory.InitializeWebGPUAsync();
            await using var game = new LoopGame();
            var context = new GameContextBrowser("#game-loop", 65, 63) { InitializeDatabase = false };
            var closed = 0;
            var exiting = 0;
            var resizes = 0;
            game.WindowCreated += (_, _) => game.Window.Closing += (_, _) => closed++;
            game.WindowCreated += (_, _) => game.Window.ClientSizeChanged += (_, _) => resizes++;
            game.Exiting += (_, _) => exiting++;
            game.Run(context);
            for (var frame = 0; frame < 4; frame++)
            {
                await NextFrame();
                context.AdvanceFrame(65, 63, true, true);
            }
            if (game.Draws != 4 || !game.IsRunning) throw new Exception("Browser GameBase did not advance four presented frames.");
            game.InactiveSleepTime = TimeSpan.FromSeconds(1);
            var started = Stopwatch.GetTimestamp();
            context.AdvanceFrame(65, 63, false, false);
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > 500 || game.Draws != 4)
                throw new Exception("Inactive browser frame blocked the event loop or drew a hidden window.");
            context.AdvanceFrame(65, 63, true, true);
            using (var staging = game.GraphicsDevice.Presenter.BackBuffer.ToStaging())
            {
                var commands = game.GraphicsContext.CommandList;
                commands.Reset();
                commands.Copy(game.GraphicsDevice.Presenter.BackBuffer, staging);
                var pixels = new uint[65 * 63];
                started = Stopwatch.GetTimestamp();
                while (!staging.GetData(commands, pixels, doNotWait: true))
                {
                    if (Stopwatch.GetElapsedTime(started).TotalSeconds > 10) throw new TimeoutException("Game frame readback timed out.");
                    await Task.Delay(1);
                }
                var blueFirst = staging.ViewFormat is PixelFormat.B8G8R8A8_UNorm or PixelFormat.B8G8R8A8_UNorm_SRgb;
                var expected = blueFirst ? 0xff4080bfU : 0xffbf8040U;
                foreach (var pixel in pixels)
                    if (pixel != expected) throw new Exception($"Browser game frame pixel {pixel:x8} != {expected:x8}.");
            }
            if (cycle == 0)
            {
                // Exit between frames must still deliver EndRun and both exit events.
                game.Exit();
                context.AdvanceFrame(65, 63, true, true);
                context.AdvanceFrame(97, 71, false, false);
                if (resizes != 0) throw new Exception("Closed browser window still processed host resize callbacks.");
                await Task.WhenAll(game.DisposeAsync().AsTask(), game.DisposeAsync().AsTask());
            }
            else
            {
                // Active shutdown, including a lifecycle handler reentering it,
                // shares one operation and stops frames before the GPU wait.
                Task? reentered = null;
                game.Exiting += (_, _) => reentered = game.DisposeAsync().AsTask();
                var disposal = game.DisposeAsync().AsTask();
                await disposal;
                if (!ReferenceEquals(reentered, disposal)) throw new Exception("Reentrant browser disposal was not coalesced.");
            }
            if (game.IsRunning || game.Ends != 1 || closed != 1 || exiting != 1 || game.Draws != 5)
                throw new Exception("Browser game exit skipped or repeated lifecycle events.");
            if (!game.IsDisposed || game.Probe.Disposals != 1) throw new Exception("Browser game systems/device were not disposed exactly once.");
            GraphicsAdapterFactory.Dispose();
        }
    }

    private sealed class LoopGame : GameBase
    {
        public int Draws, Ends;
        public readonly LifetimeSystem Probe;
        public LoopGame()
        {
            var manager = new GraphicsDeviceManager(this)
            {
                PreferredBackBufferWidth = 65, PreferredBackBufferHeight = 63,
                PreferredBackBufferFormat = PixelFormat.R8G8B8A8_UNorm,
                PreferredDepthStencilFormat = PixelFormat.None,
                PreferredColorSpace = ColorSpace.Gamma,
                PreferredGraphicsProfile = [GraphicsProfile.Level_10_0],
            };
            Services.AddService<IGraphicsDeviceManager>(manager);
            Services.AddService<IGraphicsDeviceService>(manager);
            Probe = new LifetimeSystem(this);
            GameSystems.Add(Probe);
        }
        public override void ConfirmRenderingSettings(bool gameCreation) { }
        protected override void Draw(GameTime time)
        {
            base.Draw(time);
            GraphicsContext.CommandList.Clear(GraphicsDevice.Presenter.BackBuffer, new Color4(.25f, .5f, .75f, 1));
            Draws++;
        }
        protected override void EndRun() => Ends++;
    }

    private sealed class LifetimeSystem(GameBase game) : GameSystemBase(game.Services)
    {
        public int Disposals;
        protected override void Destroy() { Disposals++; base.Destroy(); }
    }

    [JSImport("nextFrame", "managed-gpu-probe")]
    private static partial Task NextFrame();
}
