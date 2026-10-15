using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Stride.Editor.ImGui;

/// <summary>Deletes the exact validated file object while denying external writes/renames.</summary>
internal static class WindowsOwnedAssetFileDeletion
{
    internal static void Delete(string path, ReadOnlySpan<byte> expectedHash, bool requireExists = false, OwnedFileIdentity? expectedIdentity = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        const uint read = 0x80000000, delete = 0x00010000, shareRead = 1, openExisting = 3, normal = 0x80;
        using var handle = CreateFileW(Path.GetFullPath(path), read | delete, shareRead, 0, openExisting, normal | 0x00200000, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (!requireExists && (error is 2 or 3)) return;
            throw new IOException("The asset file could not be opened for deletion.", new Win32Exception(error));
        }
        using var stream = new FileStream(handle, FileAccess.Read);
        if (expectedIdentity != null && WindowsOwnedAssetFileWriter.CaptureIdentity(stream) != expectedIdentity.Value)
            throw new IOException("The publication file was replaced outside the editor.");
        if (!SHA256.HashData(stream).AsSpan().SequenceEqual(expectedHash))
            throw new IOException("The removed asset changed on disk. Reload the project before saving.");
        var disposition = new FileDisposition { DeleteFile = 1 };
        if (!SetFileInformationByHandle(handle, 4, ref disposition, 1))
            throw new IOException("The asset file could not be deleted.", new Win32Exception(Marshal.GetLastWin32Error()));
        // Closing this handle deletes the validated object. There is intentionally
        // no subsequent File.Delete(path), which could target a replacement object.
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileDisposition { public byte DeleteFile; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref FileDisposition information, uint size);
}
