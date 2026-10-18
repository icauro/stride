using System.Runtime.InteropServices.JavaScript;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Input;
using Stride.Core.Diagnostics;

[SupportedOSPlatform("browser")]
public static partial class Program
{
    private static Game? game;
    private static GameContextBrowser? context;
    private static InputSourceBrowser Source => (InputSourceBrowser)game!.Input.Sources.Single();
    public static void Main()
    {
        GlobalLogger.GlobalMessageLogged += message => ReportLog($"[{message.Module}]: {message.Type}: {message.Text}", (int)message.Type);
    }

    [JSExport]
    public static async Task Start(string manifestUrl, int width, int height)
    {
        if (game != null) throw new InvalidOperationException("Space Escape is already started.");
        RuntimeHelpers.RunModuleConstructor(typeof(SpaceEscape.GameScript).Module.ModuleHandle);
        using var http = new HttpClient();
        await BrowserContent.PreloadAsync(http, new Uri(manifestUrl));
        await GraphicsAdapterFactory.InitializeWebGPUAsync();
        game = new Game();
        game.GraphicsDeviceManager.PreferredBackBufferWidth = width;
        game.GraphicsDeviceManager.PreferredBackBufferHeight = height;
        context = new GameContextBrowser("#game", width, height);
        try { game.Run(context); }
        catch { await Stop(); throw; }
    }
    [JSExport] public static void Frame(int width, int height, bool focused, bool visible)
    {
        try { context!.AdvanceFrame(width, height, focused, visible); }
        catch (Exception error) { ReportError(error.ToString()); throw; }
    }
    [JSImport("managedError", "space-escape")] private static partial void ReportError(string error);
    [JSImport("log", "space-escape")] private static partial void ReportLog(string message, int level);
    [JSExport] public static int Entities() => game?.SceneSystem.SceneInstance?.RootScene?.Entities.Count ?? 0;
    [JSExport] public static bool Key(string code, bool down) => Source.SubmitKey(code, down);
    [JSExport] public static void Move(float x, float y) => Source.SubmitMouseMove(x, y);
    [JSExport] public static void Button(int button, bool down) => Source.SubmitMouseButton(button, down);
    [JSExport] public static void Wheel(float ticks) => Source.SubmitWheel(ticks);
    [JSExport] public static void Pointer(int id, int type, float x, float y) => Source.SubmitPointer(id, (PointerEventType)type, x, y);
    [JSExport] public static void Surface(int width, int height) => Source.SetSurfaceSize(width, height);
    [JSExport] public static void CancelMouse() => Source.CancelMouse();
    [JSExport] public static void Pause() => Source.Pause();
    [JSExport] public static double Frames() => game?.DrawTime.FrameCount ?? 0;
    [JSExport] public static async Task Stop()
    {
        if (game == null) return;
        await game.DisposeAsync();
        game = null;
        context = null;
        GraphicsAdapterFactory.Dispose();
    }
}
