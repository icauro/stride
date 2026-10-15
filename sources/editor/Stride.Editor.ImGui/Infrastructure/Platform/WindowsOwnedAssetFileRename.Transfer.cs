using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Stride.Editor.ImGui;

internal sealed partial class WindowsOwnedAssetFileRename
{
    private void Transfer(string destination)
    {
        // Both file objects remain locked against external replacement throughout publication.
        using var directory = CreateFileW(Path.GetDirectoryName(destination)!, 0x80, 1 | 2, 0, 3, 0x02000000, 0);
        if (directory.IsInvalid) throw new IOException("The asset destination directory is unavailable.", new Win32Exception(Marshal.GetLastWin32Error()));
        var nextHandle = CreateFileW(destination, 0x80000000 | 0x40000000 | 0x00010000, 1, 0, 1, 0x80 | 0x00200000, 0);
        if (nextHandle.IsInvalid) { nextHandle.Dispose(); throw new IOException("The asset destination is occupied or unavailable.", new Win32Exception(Marshal.GetLastWin32Error())); }
        FileStream? nextStream = null;
        var published = false;
        try
        {
            nextStream = new FileStream(nextHandle, FileAccess.ReadWrite);
            if (!GetFileTime(handle, out var creation, out var access, out var modified))
                throw new IOException("The source file times could not be read.", new Win32Exception(Marshal.GetLastWin32Error()));
            stream.Position = 0;
            var fingerprint = SHA256.HashData(stream);
            stream.Position = 0; stream.CopyTo(nextStream); nextStream.Flush(true);
            nextStream.Position = 0;
            if (!SHA256.HashData(nextStream).AsSpan().SequenceEqual(fingerprint)) throw new IOException("The transferred asset content could not be verified.");
            if (!SetFileTime(nextHandle, ref creation, ref access, ref modified))
                throw new IOException("The transferred asset file times could not be preserved.", new Win32Exception(Marshal.GetLastWin32Error()));
            var disposition = new TransferDisposition { DeleteFile = 1 };
            if (!SetTransferDisposition(handle, 4, ref disposition, 1))
                throw new IOException("The owned source file could not be removed after transfer.", new Win32Exception(Marshal.GetLastWin32Error()));
            var previousStream = stream;
            handle = nextHandle; stream = nextStream; currentPath = destination; published = true;
            previousStream.Dispose();
        }
        catch (Exception failure)
        {
            if (published) throw;
            try
            {
                var disposition = new TransferDisposition { DeleteFile = 1 };
                if (!SetTransferDisposition(nextHandle, 4, ref disposition, 1))
                    throw new IOException("The new asset file could not be removed during transfer recovery.", new Win32Exception(Marshal.GetLastWin32Error()));
            }
            catch (Exception recovery) { throw new AggregateException("The asset transfer failed and its new file could not be removed.", failure, recovery); }
            throw;
        }
        finally { if (!published) { if (nextStream != null) nextStream.Dispose(); else nextHandle.Dispose(); } }
    }
    [StructLayout(LayoutKind.Sequential)] private struct TransferDisposition { public byte DeleteFile; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeFileTime { public uint Low; public uint High; }
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetTransferDisposition(SafeFileHandle file, int kind, ref TransferDisposition information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileTime(SafeFileHandle file, out NativeFileTime creation, out NativeFileTime access, out NativeFileTime modified);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileTime(SafeFileHandle file, ref NativeFileTime creation, ref NativeFileTime access, ref NativeFileTime modified);
}
