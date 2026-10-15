using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace Stride.GameStudio.Diagnostics.VisualCapture;

/// <summary>Loads WinRT capture only for opt-in diagnostics, preserving Game Studio's existing TFM.</summary>
internal sealed class WindowGpuCaptureBridge : IDisposable
{
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, "VisualCapture", "Windows");
    private MethodInfo capture;
    private bool resolving;
    private Assembly Resolve(AssemblyLoadContext context, AssemblyName name)
    {
        var file = Path.Combine(directory, name.Name + ".dll");
        return File.Exists(file) ? context.LoadFromAssemblyPath(file) : null;
    }
    internal async Task<System.Text.Json.JsonElement> Capture(CaptureTarget target, string output, int timeoutMs, CancellationToken cancellation)
    {
        if (capture == null)
        {
            var file = Path.Combine(directory, "Stride.Editor.VisualCapture.Windows.dll");
            if (!File.Exists(file)) throw new CaptureEndpointException("capture_backend_missing", "The Windows Graphics Capture backend is not deployed.");
            if (!resolving) { AssemblyLoadContext.Default.Resolving += Resolve; resolving = true; }
            capture = AssemblyLoadContext.Default.LoadFromAssemblyPath(file)
                .GetType("Stride.Editor.VisualCapture.Windows.WindowGpuCapture", true).GetMethod("CaptureAsync", BindingFlags.Public | BindingFlags.Static);
        }
        var rect = target.Rect;
        Task<string> result;
        try { result = (Task<string>)capture.Invoke(null, [CaptureNativeBounds.ParseHandle(target.Hwnd), rect.X, rect.Y, rect.Width, rect.Height, output, timeoutMs, cancellation]); }
        catch (TargetInvocationException error) { throw new CaptureEndpointException("window_capture_failed", error.InnerException?.Message ?? error.Message); }
        try
        {
            using var metadata = System.Text.Json.JsonDocument.Parse(await result);
            return metadata.RootElement.Clone();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { throw new CaptureEndpointException("window_capture_failed", error.Message); }
    }
    public void Dispose() { if (resolving) AssemblyLoadContext.Default.Resolving -= Resolve; resolving = false; }
}
