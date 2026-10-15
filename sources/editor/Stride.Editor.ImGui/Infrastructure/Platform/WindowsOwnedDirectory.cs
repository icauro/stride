using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Stride.Editor.ImGui;

internal readonly record struct OwnedDirectoryIdentity(uint Volume, uint High, uint Low);
internal static class WindowsOwnedDirectory
{
    private sealed class CreationRecovery(SafeFileHandle handle)
    {
        private bool deleteRequested;
        internal void Complete()
        {
            if (!deleteRequested)
            {
                MarkDeleted(handle);
                deleteRequested = true;
            }
            handle.Dispose();
        }
    }
    private static readonly List<CreationRecovery> failedCreations = [];
    internal static bool HasFailedCreations { get { lock (failedCreations) return failedCreations.Count != 0; } }
    internal static void RetryFailedCreations()
    {
        lock (failedCreations)
        {
            List<Exception>? errors = null;
            for (var index = failedCreations.Count - 1; index >= 0; index--)
            {
                try { failedCreations[index].Complete(); failedCreations.RemoveAt(index); }
                catch (Exception error) { (errors ??= []).Add(error); }
            }
            if (errors != null) throw new AggregateException("The owned directory creation requires recovery.", errors);
        }
    }
    internal static OwnedDirectoryIdentity Create(string path, OwnedDirectoryIdentity? expectedParent = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parentPath = Path.GetDirectoryName(path) ?? throw new IOException("The source directory root cannot be created.");
        // NtCreateFile FILE_CREATE returns the created object's handle atomically.
        // A pinned actual parent and relative leaf avoid reopening an uncertain path.
        using var parent = Open(parentPath, false, traverse: true);
        var parentIdentity = Identity(parent);
        if (expectedParent != null && parentIdentity != expectedParent.Value)
            throw new IOException("The source directory parent changed outside the editor.");
        var leaf = Path.GetFileName(path);
        var byteLength = checked((ushort)(leaf.Length * sizeof(char)));
        var maximumLength = checked((ushort)(byteLength + sizeof(char)));
        var text = Marshal.StringToHGlobalUni(leaf);
        nint namePointer = 0;
        SafeFileHandle handle;
        try
        {
            namePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeName>());
            var name = new UnicodeName { Length = byteLength, MaximumLength = maximumLength, Buffer = text };
            Marshal.StructureToPtr(name, namePointer, false);
            var attributes = new ObjectAttributes
            {
                Length = (uint)Marshal.SizeOf<ObjectAttributes>(), RootDirectory = parent.DangerousGetHandle(),
                ObjectName = namePointer, Attributes = 0x40,
            };
            const uint readAttributesDeleteSynchronize = 0x80 | 0x10000 | 0x100000;
            var status = NtCreateFile(out handle, readAttributesDeleteSynchronize, ref attributes,
                out _, 0, 0x80, 1 | 2, 2, 1 | 0x20, 0, 0);
            GC.KeepAlive(parent);
            if (status < 0)
            {
                handle.Dispose();
                throw new IOException("The source directory could not be created.", new Win32Exception(unchecked((int)RtlNtStatusToDosError(status))));
            }
        }
        finally { Marshal.FreeHGlobal(namePointer); Marshal.FreeHGlobal(text); }
        OwnedDirectoryIdentity identity;
        try { identity = Identity(handle); }
        catch (Exception failure)
        {
            // Retain the exact new-object handle before compensation. No path lookup
            // is ever used to adopt or delete a directory lacking an identity receipt.
            var recovery = new CreationRecovery(handle);
            lock (failedCreations)
            {
                failedCreations.Add(recovery);
                try { recovery.Complete(); failedCreations.Remove(recovery); }
                catch (Exception cleanup) { throw new AggregateException("The source directory creation could not be restored.", failure, cleanup); }
            }
            throw;
        }
        handle.Dispose();
        return identity;
    }
    internal static OwnedDirectoryIdentity Capture(string path) { using var handle = Open(path, false); return Identity(handle); }
    internal static SafeFileHandle CreateNewFile(string path, OwnedDirectoryIdentity expectedParent)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        path = Path.GetFullPath(path);
        using var parent = Open(Path.GetDirectoryName(path) ?? throw new IOException("The file parent is unavailable."), false, traverse: true);
        if (Identity(parent) != expectedParent) throw new IOException("The publication directory changed outside the editor.");
        var leaf = Path.GetFileName(path);
        var byteLength = checked((ushort)(leaf.Length * sizeof(char)));
        var text = Marshal.StringToHGlobalUni(leaf);
        nint namePointer = 0;
        try
        {
            namePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeName>());
            var name = new UnicodeName { Length = byteLength, MaximumLength = checked((ushort)(byteLength + sizeof(char))), Buffer = text };
            Marshal.StructureToPtr(name, namePointer, false);
            var attributes = new ObjectAttributes { Length = (uint)Marshal.SizeOf<ObjectAttributes>(), RootDirectory = parent.DangerousGetHandle(), ObjectName = namePointer, Attributes = 0x40 };
            const uint readWriteDeleteSynchronize = 0x80000000 | 0x40000000 | 0x10000 | 0x100000;
            // FILE_CREATE, relative to the certified pinned parent; never follows
            // a final reparse point or replaces an existing namespace entry.
            var status = NtCreateFile(out var handle, readWriteDeleteSynchronize, ref attributes, out _, 0, 0x80, 1, 2,
                0x20 | 0x40 | 0x00200000, 0, 0);
            GC.KeepAlive(parent);
            if (status < 0)
            { handle.Dispose(); throw new IOException("The new publication file could not be created.", new Win32Exception(unchecked((int)RtlNtStatusToDosError(status)))); }
            return handle;
        }
        finally { if (namePointer != 0) Marshal.FreeHGlobal(namePointer); Marshal.FreeHGlobal(text); }
    }
    internal static void Remove(string path, OwnedDirectoryIdentity expected)
    {
        using var handle = Open(path);
        if (Identity(handle) != expected) throw new IOException("The source directory changed outside the editor.");
        MarkDeleted(handle);
    }
    private static void MarkDeleted(SafeFileHandle handle)
    {
        var disposition = new FileDisposition { DeleteFile = 1 };
        if (!SetFileInformationByHandle(handle, 4, ref disposition, 1))
            throw new IOException("The owned source directory is no longer empty or available.", new Win32Exception(Marshal.GetLastWin32Error()));
    }
    private static SafeFileHandle Open(string path, bool deleting = true, bool traverse = false)
    {
        var handle = CreateFileW(path, 0x00000080 | (deleting ? 0x00010000u : 0) | (traverse ? 0x20u : 0), 1 | 2, 0, 3, 0x02000000 | 0x00200000, 0);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("The source directory could not be opened.", new Win32Exception(Marshal.GetLastWin32Error())); }
        return handle;
    }
    private static OwnedDirectoryIdentity Identity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x10) == 0 || (info.Attributes & 0x400) != 0)
            throw new IOException("The source directory is unavailable or redirected.");
        return new(info.Volume, info.IndexHigh, info.IndexLow);
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileDisposition { public byte DeleteFile; }
    [StructLayout(LayoutKind.Sequential)] private struct FileInfo
    {
        public uint Attributes, CreatedLow, CreatedHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeName
    {
        public ushort Length, MaximumLength;
        public nint Buffer;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes
    {
        public uint Length;
        public nint RootDirectory, ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor, SecurityQualityOfService;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatusBlock
    {
        public nint Status;
        public nuint Information;
    }
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes,
        out IoStatusBlock status, nint allocationSize, uint fileAttributes, uint shareAccess, uint creation,
        uint options, nint extendedAttributes, uint extendedAttributesLength);
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern uint RtlNtStatusToDosError(int status);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref FileDisposition disposition, uint size);
}
