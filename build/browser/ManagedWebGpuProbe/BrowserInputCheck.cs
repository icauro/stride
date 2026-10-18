using System;
using System.Linq;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Stride.Games;
using Stride.Input;

[SupportedOSPlatform("browser")]
public static partial class BrowserInputCheck
{
    private static InputManager? input;
    private static InputSourceBrowser Source => (InputSourceBrowser)input!.Sources.Single();
    public static async Task RunAsync()
    {
        if (!Requested()) return;
        using var manager = new InputManager();
        input = manager;
        try
        {
            manager.Initialize(new GameContextBrowser("#input-canvas"));
            await AttachAndWait();
        }
        finally { Detach(); input = null; }
    }
    [JSExport] public static void Step() => input!.Update(new GameTime(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.0 / 60)));
    [JSExport] public static bool Key(string code, bool down) => Source.SubmitKey(code, down);
    [JSExport] public static void Move(float x, float y) => Source.SubmitMouseMove(x, y);
    [JSExport] public static void Button(int button, bool down) => Source.SubmitMouseButton(button, down);
    [JSExport] public static void Wheel(float ticks) => Source.SubmitWheel(ticks);
    [JSExport] public static void Pointer(int id, int type, float x, float y) => Source.SubmitPointer(id, (PointerEventType)type, x, y);
    [JSExport] public static void Surface(int width, int height) => Source.SetSurfaceSize(width, height);
    [JSExport] public static void CancelMouse() => Source.CancelMouse();
    [JSExport] public static void Pause() => Source.Pause();
    [JSExport] public static bool Down() => input!.IsKeyDown(Keys.Left);
    [JSExport] public static bool Pressed() => input!.IsKeyPressed(Keys.Left);
    [JSExport] public static bool Released() => input!.IsKeyReleased(Keys.Left);
    [JSExport] public static bool MouseDown() => input!.IsMouseButtonDown(MouseButton.Left);
    [JSExport] public static float MouseX() => input!.MousePosition.X;
    [JSExport] public static float MouseY() => input!.MousePosition.Y;
    [JSExport] public static int Touches() => Source.Devices.Values.OfType<IPointerDevice>().Single(device => device is not IMouseDevice).DownPointers.Count;
    [JSImport("inputRequested", "managed-gpu-probe")] private static partial bool Requested();
    [JSImport("attachInputAndWait", "managed-gpu-probe")] private static partial Task AttachAndWait();
    [JSImport("detachInput", "managed-gpu-probe")] private static partial void Detach();
}
