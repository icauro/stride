using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Stride.Editor.ImGui;

/// <summary>Checks the physical file addressed by a retained source read handle.</summary>
internal static class WindowsSourceFileIdentity
{
    internal readonly record struct Identity(uint Volume, uint IndexHigh, uint IndexLow, uint SizeHigh, uint SizeLow);
    internal static Identity Capture(FileStream stream)
    {
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(info.Volume, info.IndexHigh, info.IndexLow, info.SizeHigh, info.SizeLow);
    }
    internal static void Validate(string path, Identity expected)
    {
        using var current = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Capture(current) != expected) throw new IOException("The source file changed during import: " + path);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInfo
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out NativeInfo info);
}
