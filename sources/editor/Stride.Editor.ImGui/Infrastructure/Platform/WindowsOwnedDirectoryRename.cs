using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Stride.Editor.ImGui;

internal sealed class WindowsOwnedDirectoryRename : IDisposable
{
    private readonly SafeFileHandle handle;
    private string currentPath;
    internal WindowsOwnedDirectoryRename(string path, OwnedDirectoryIdentity expected)
    {
        currentPath = Path.GetFullPath(path);
        handle = CreateFileW(currentPath, 0x00000080 | 0x00010000, 1 | 2, 0, 3, 0x02000000 | 0x00200000, 0);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("The folder could not be opened for relocation.", new Win32Exception(Marshal.GetLastWin32Error())); }
        if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x10) == 0 || (info.Attributes & 0x400) != 0
            || new OwnedDirectoryIdentity(info.Volume, info.IndexHigh, info.IndexLow) != expected)
        { handle.Dispose(); throw new IOException("The folder changed outside the editor."); }
    }
    internal void Move(string path, OwnedDirectoryIdentity? expectedParent = null)
    {
        path = Path.GetFullPath(path);
        if (string.Equals(currentPath, path, StringComparison.Ordinal)) return;
        if (string.Equals(currentPath, path, StringComparison.OrdinalIgnoreCase))
        {
            var previous = currentPath;
            Rename(Path.Combine(Path.GetDirectoryName(previous)!, ".stride-case-rename-" + Guid.NewGuid().ToString("N")));
            try { Rename(path, expectedParent); }
            catch (Exception failure)
            {
                try { Rename(previous); }
                catch (Exception recovery) { throw new AggregateException("The case-only folder rename could not be restored.", failure, recovery); }
                throw;
            }
        }
        else Rename(path, expectedParent);
    }
    private void Rename(string path, OwnedDirectoryIdentity? expectedParent = null)
    {
        using var directory = CreateFileW(Path.GetDirectoryName(path)!, 0x80, 1 | 2, 0, 3, 0x02000000 | 0x00200000, 0);
        if (directory.IsInvalid) throw new IOException("The asset destination directory is unavailable.", new Win32Exception(Marshal.GetLastWin32Error()));
        if (expectedParent is { } expected && (!GetFileInformationByHandle(directory, out var info)
            || (info.Attributes & 0x10) == 0 || (info.Attributes & 0x400) != 0
            || new OwnedDirectoryIdentity(info.Volume, info.IndexHigh, info.IndexLow) != expected))
            throw new IOException("The destination directory changed outside the editor.");
        var bytes = Encoding.Unicode.GetBytes(Path.GetFileName(path));
        var offset = Marshal.OffsetOf<FileRenameHeader>(nameof(FileRenameHeader.FileName)).ToInt32();
        var size = checked(offset + bytes.Length);
        var information = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, information, size);
            Marshal.WriteIntPtr(information, Marshal.OffsetOf<FileRenameHeader>(nameof(FileRenameHeader.RootDirectory)).ToInt32(), directory.DangerousGetHandle());
            Marshal.WriteInt32(information, Marshal.OffsetOf<FileRenameHeader>(nameof(FileRenameHeader.FileNameLength)).ToInt32(), bytes.Length);
            Marshal.Copy(bytes, 0, information + offset, bytes.Length);
            if (!SetFileInformationByHandle(handle, 3, information, (uint)size))
                throw new IOException("The owned folder could not be renamed.", new Win32Exception(Marshal.GetLastWin32Error()));
            currentPath = path;
        }
        finally { Marshal.FreeHGlobal(information); }
    }
    public void Dispose() => handle.Dispose();
    [StructLayout(LayoutKind.Sequential)] private struct FileInfo
    { public uint Attributes, CreatedLow, CreatedHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfo info);
    [StructLayout(LayoutKind.Sequential)] private struct FileRenameHeader
    { public byte ReplaceIfExists; public nint RootDirectory; public uint FileNameLength; public char FileName; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, nint information, uint size);
}
