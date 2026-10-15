using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Stride.Editor.ImGui;

internal sealed partial class WindowsOwnedAssetFileRename : IDisposable
{
    private SafeFileHandle handle;
    private FileStream stream;
    private string currentPath;
    internal string CurrentPath => currentPath;
    internal WindowsOwnedAssetFileRename(string path, ReadOnlySpan<byte> fingerprint)
    {
        currentPath = Path.GetFullPath(path);
        handle = CreateFileW(currentPath, 0x80000000 | 0x00010000, 1, 0, 3, 0x80 | 0x00200000, 0);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("The asset file could not be opened for relocation.", new Win32Exception(Marshal.GetLastWin32Error())); }
        stream = new FileStream(handle, FileAccess.Read);
        if (!SHA256.HashData(stream).AsSpan().SequenceEqual(fingerprint))
        { stream.Dispose(); throw new IOException("The asset changed on disk. Reload it before moving it."); }
    }
    internal void Move(string path)
    {
        path = Path.GetFullPath(path);
        if (string.Equals(currentPath, path, StringComparison.Ordinal)) return;
        if (string.Equals(currentPath, path, StringComparison.OrdinalIgnoreCase))
        {
            var previous = currentPath;
            Rename(Path.Combine(Path.GetDirectoryName(previous)!, ".stride-case-rename-" + Guid.NewGuid().ToString("N")));
            try { Rename(path); }
            catch (Exception failure)
            {
                try { Rename(previous); }
                catch (Exception recovery) { throw new AggregateException("The case-only asset rename could not be restored.", failure, recovery); }
                throw;
            }
        }
        else
        {
            try { Rename(path); }
            catch (IOException error) when (error.InnerException is Win32Exception { NativeErrorCode: 17 }) { Transfer(path); }
        }
    }
    private void Rename(string path)
    {
        using var directory = CreateFileW(Path.GetDirectoryName(path)!, 0x80 | 0x20, 1 | 2, 0, 3, 0x02000000, 0);
        if (directory.IsInvalid) throw new IOException("The asset destination directory is unavailable.", new Win32Exception(Marshal.GetLastWin32Error()));
        var bytes = Encoding.Unicode.GetBytes(Path.GetFileName(path));
        var offset = Marshal.OffsetOf<FileRenameHeader>(nameof(FileRenameHeader.FileName)).ToInt32();
        var size = Math.Max(Marshal.SizeOf<FileRenameHeader>(), checked(offset + bytes.Length + sizeof(char)));
        var information = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, information, size);
            Marshal.WriteIntPtr(information, Marshal.OffsetOf<FileRenameHeader>(nameof(FileRenameHeader.RootDirectory)).ToInt32(), directory.DangerousGetHandle());
            Marshal.WriteInt32(information, Marshal.OffsetOf<FileRenameHeader>(nameof(FileRenameHeader.FileNameLength)).ToInt32(), bytes.Length);
            Marshal.Copy(bytes, 0, information + offset, bytes.Length);
            // Win32 normalizes FileName before issuing the native rename, which
            // breaks the directory-relative contract. Pass the verified source
            // and retained destination directory directly to the native API.
            var status = NtSetInformationFile(handle, out _, information, (uint)size, 10);
            if (status < 0)
                throw new IOException("The owned asset file could not be renamed.", new Win32Exception(unchecked((int)RtlNtStatusToDosError(status))));
            currentPath = path;
        }
        finally { Marshal.FreeHGlobal(information); }
    }
    public void Dispose() => stream.Dispose();
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct FileRenameHeader
    { public byte ReplaceIfExists; public nint RootDirectory; public uint FileNameLength; public char FileName; }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatusBlock
    { public nint Status; public nuint Information; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtSetInformationFile(SafeFileHandle file, out IoStatusBlock ioStatus, nint information, uint size, int kind);
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern uint RtlNtStatusToDosError(int status);
}
