using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.IO;
using Stride.Core;
using Stride.Graphics;
using Stride.Games;
using Stride.Core.Mathematics;

[SupportedOSPlatform("browser")]
public static partial class Program
{
    public static void Main()
    {
        if (!OperatingSystem.IsBrowser())
            throw new PlatformNotSupportedException("This smoke app must run in a browser.");

        if (Platform.Type != PlatformType.Browser || Platform.IsWindowsDesktop || (int)PlatformType.macOS != 6 || (int)PlatformType.Browser != 7)
            throw new InvalidOperationException("Stride did not select Browser or changed existing platform values.");
        var temporaryFile = Path.Combine(PlatformFolders.ApplicationTemporaryDirectory, "stride-browser-core-smoke.txt");
        File.WriteAllText(temporaryFile, "Stride browser filesystem");
        if (File.ReadAllText(temporaryFile) != "Stride browser filesystem")
            throw new InvalidOperationException("Browser temporary filesystem roundtrip failed.");
        File.Delete(temporaryFile);
        if (!Path.IsPathRooted(PlatformFolders.ApplicationDataDirectory))
            throw new InvalidOperationException("Browser assets directory must be rooted in the virtual filesystem.");
        ReportCore(Platform.Type.ToString(), PlatformFolders.ApplicationDataDirectory);
        // A locally generated RGBA PNG exercises the actual portable image decoder.
        using (var image = Image.Load(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGMQVDL+DwACFAFmBODefwAAAABJRU5ErkJggg==")))
        {
            var data = image.PixelBuffer[0].DataPointer;
            if (image.Description.Width != 1 || image.Description.Height != 1 || image.Description.Format != PixelFormat.B8G8R8A8_UNorm ||
                Marshal.ReadByte(data, 0) != 51 || Marshal.ReadByte(data, 1) != 34 || Marshal.ReadByte(data, 2) != 17 || Marshal.ReadByte(data, 3) != 255)
                throw new InvalidOperationException("Browser PNG decode returned incorrect pixels.");
            ReportImage();
        }

        VerifyWindow();
        ReportStartup(RuntimeInformation.FrameworkDescription);
    }

    private static void VerifyWindow()
    {
        using var game = new WindowTestGame();
        var context = new GameContextBrowser("#test-canvas", 65, 63);
        using var window = game.Services.GetService<IGamePlatform>().CreateWindow(context);
        if (window.ClientBounds.Width != 65 || window.ClientBounds.Height != 63 ||
            window.NativeWindow.Context != AppContextType.Browser || (string)window.NativeWindow.NativeWindow != "#test-canvas")
            throw new InvalidOperationException("Browser canvas context was not preserved.");
        var resizes = 0;
        var closes = 0;
        window.ClientSizeChanged += (_, _) => resizes++;
        window.Closing += (_, _) => closes++;
        window.SetSize(new Int2(130, 126));
        if (resizes != 1 || window.ClientBounds.Width != 130 || window.ClientBounds.Height != 126)
            throw new InvalidOperationException("Browser resize must notify exactly once.");
        window.Title = "Stride browser test";
        window.IsMouseVisible = false;
        window.Opacity = 0.5;
        if (context.Title != "Stride browser test" || context.IsMouseVisible || context.Opacity != 0.5)
            throw new InvalidOperationException("Browser window state did not reach the host context.");
        try
        {
            context.AdvanceFrame(65, 63, true, true);
            throw new Exception("Advancing an unstarted game must fail.");
        }
        catch (InvalidOperationException) { }
        window.Dispose();
        window.Dispose();
        if (closes != 1) throw new InvalidOperationException("Browser close must notify exactly once.");
        ReportWindow();
    }

    private sealed class WindowTestGame : GameBase
    {
        public override void ConfirmRenderingSettings(bool gameCreation) { }
    }

    [JSImport("reportStartup", "smoke")]
    private static partial void ReportStartup(string framework);

    [JSImport("reportCore", "smoke")]
    private static partial void ReportCore(string platform, string assetDirectory);

    [JSImport("reportImage", "smoke")]
    private static partial void ReportImage();

    [JSImport("reportWindow", "smoke")]
    private static partial void ReportWindow();

    [JSExport]
    public static int Add(int left, int right) => checked(left + right);
}
