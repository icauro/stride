using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Stride.Editor.ImGui;

/// <summary>Finds the actual open solution through Visual Studio's automation object.</summary>
internal static class WindowsVisualStudioInstance
{
    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint apartment);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(uint reserved, out IRunningObjectTable table);
    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(uint reserved, out IBindCtx context);

    public static bool TryActivate(string solutionPath, CancellationToken cancellation = default)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var initialized = CoInitializeEx(0, 2); // COINIT_APARTMENTTHREADED
        if (initialized < 0) return false;
        IRunningObjectTable? table = null;
        IBindCtx? context = null;
        IEnumMoniker? enumerator = null;
        try
        {
            Marshal.ThrowExceptionForHR(GetRunningObjectTable(0, out table));
            Marshal.ThrowExceptionForHR(CreateBindCtx(0, out context));
            table.EnumRunning(out enumerator);
            var monikers = new IMoniker[1];
            while (enumerator.Next(1, monikers, 0) == 0)
            {
                object? automation = null, solution = null, window = null;
                var moniker = monikers[0];
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    moniker.GetDisplayName(context, null, out var name);
                    if (!name.StartsWith("!VisualStudio.DTE", StringComparison.OrdinalIgnoreCase)) continue;
                    Marshal.ThrowExceptionForHR(table.GetObject(moniker, out automation));
                    if (automation == null) continue;
                    solution = GetProperty(automation, "Solution");
                    if (solution == null || GetProperty(solution, "FullName") is not string opened
                        || string.IsNullOrWhiteSpace(opened)
                        || !string.Equals(Path.GetFullPath(opened), Path.GetFullPath(solutionPath), StringComparison.OrdinalIgnoreCase))
                        continue;
                    window = GetProperty(automation, "MainWindow");
                    if (window == null) continue;
                    cancellation.ThrowIfCancellationRequested();
                    window.GetType().InvokeMember("Activate", BindingFlags.InvokeMethod, null, window, null);
                    return true;
                }
                catch (Exception error) when (error is COMException or TargetInvocationException or ArgumentException or IOException or MissingMemberException or InvalidComObjectException)
                {
                    // A closing or busy IDE must not prevent opening the requested solution.
                    System.Diagnostics.Debug.WriteLine(error.Message);
                }
                finally
                {
                    Release(window); Release(solution); Release(automation); Release(moniker);
                    monikers[0] = null!;
                }
            }
        }
        catch (COMException error) { System.Diagnostics.Debug.WriteLine(error.Message); }
        finally { Release(enumerator); Release(context); Release(table); CoUninitialize(); }
        return false;
    }

    private static object? GetProperty(object owner, string name)
        => owner.GetType().InvokeMember(name, BindingFlags.GetProperty, null, owner, null);

    private static void Release(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }
}
