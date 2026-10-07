using System.Runtime.InteropServices;

// Used only in an injected diagnostic run; capture runs never produce timing results.
static class RenderDocDiagnostic
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetApi(int version, out IntPtr api);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TriggerCapture();

    public static void Trigger()
    {
        if (!NativeLibrary.TryLoad("renderdoc.dll", out var library))
            throw new InvalidOperationException("Launch this diagnostic through renderdoccmd capture.");
        var getApi = Marshal.GetDelegateForFunctionPointer<GetApi>(NativeLibrary.GetExport(library, "RENDERDOC_GetAPI"));
        if (getApi(10600, out var api) != 1) throw new InvalidOperationException("RenderDoc API 1.6 unavailable.");
        var trigger = Marshal.GetDelegateForFunctionPointer<TriggerCapture>(Marshal.ReadIntPtr(api, 15 * IntPtr.Size));
        trigger();
        Console.WriteLine("RenderDoc capture requested; diagnostic run will exit without benchmark results.");
    }
}
