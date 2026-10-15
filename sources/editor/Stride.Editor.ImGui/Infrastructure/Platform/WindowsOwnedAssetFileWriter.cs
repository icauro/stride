using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Stride.Editor.ImGui;

internal readonly record struct OwnedFileIdentity(uint Volume, uint High, uint Low);
internal readonly record struct OwnedFileWriteResult(byte[] ContentHash, long ModifiedTicks, long Length)
{
    internal OwnedFileIdentity Identity { get; init; }
}

/// <summary>Writes and recovers the exact original file while denying external replacement.</summary>
internal static class WindowsOwnedAssetFileWriter
{
    private sealed class WriteReceipt(SafeFileHandle handle, bool isNew, byte[] written, byte[] writtenHash)
    {
        private byte[]? original;
        private FileTime originalWriteTime;
        private bool mutationStarted, bytesWritten, lengthWritten, durable;
        private bool originalBytesRestored, originalLengthRestored, originalTimeRestored, originalDurable;
        private bool deleteRequested, closed;
        internal OwnedFileWriteResult Apply(byte[]? expectedHash)
        {
            var before = Information(handle);
            if ((before.Attributes & (0x10 | 0x400)) != 0)
                throw new IOException("The authored file is unavailable or redirected.");
            originalWriteTime = new() { Low = before.WriteLow, High = before.WriteHigh };
            var length = RandomAccess.GetLength(handle);
            if (length > Array.MaxLength) throw new IOException("The authored file is too large to preserve during saving.");
            var snapshot = new byte[checked((int)length)];
            for (var offset = 0; offset < snapshot.Length;)
            {
                var read = RandomAccess.Read(handle, snapshot.AsSpan(offset), offset);
                if (read == 0) throw new EndOfStreamException("The authored file could not be snapshotted completely.");
                offset += read;
            }
            original = snapshot;
            if (expectedHash != null && !SHA256.HashData(snapshot).AsSpan().SequenceEqual(expectedHash))
                throw new IOException("The authored file changed on disk. Reload it before saving.");
            mutationStarted = true;
            RandomAccess.Write(handle, written, 0); bytesWritten = true;
            RandomAccess.SetLength(handle, written.LongLength); lengthWritten = true;
            RandomAccess.FlushToDisk(handle); durable = true;
            // Metadata failure after durable writing is still an unpublished write;
            // the catch path restores through this same owning handle.
            var after = Information(handle);
            var modified = DateTime.FromFileTimeUtc(unchecked((long)(((ulong)after.WriteHigh << 32) | after.WriteLow))).Ticks;
            if (!bytesWritten || !lengthWritten || !durable)
                throw new InvalidOperationException("The owned file write is incomplete.");
            return new(writtenHash, modified, written.LongLength) { Identity = new(after.Volume, after.IndexHigh, after.IndexLow) };
        }
        internal void Close()
        {
            if (closed) return;
            handle.Dispose();
            closed = true;
        }
        internal void Recover()
        {
            if (isNew)
            {
                if (!deleteRequested)
                {
                    var disposition = new FileDisposition { DeleteFile = 1 };
                    if (!SetFileInformationByHandle(handle, 4, ref disposition, 1))
                        throw new IOException("The newly created file could not be removed during recovery.", new Win32Exception(Marshal.GetLastWin32Error()));
                    deleteRequested = true;
                }
            }
            else if (mutationStarted)
            {
                var bytes = original ?? throw new InvalidOperationException("The original owned file bytes are unavailable.");
                // Each phase leaves the receipt only after actual native success.
                // The held handle denies external writes/replacement during retries.
                if (!originalBytesRestored) { RandomAccess.Write(handle, bytes, 0); originalBytesRestored = true; }
                if (!originalLengthRestored) { RandomAccess.SetLength(handle, bytes.LongLength); originalLengthRestored = true; }
                if (!originalTimeRestored)
                {
                    if (!SetFileTime(handle, 0, 0, ref originalWriteTime))
                        throw new IOException("The original authored file timestamp could not be restored.", new Win32Exception(Marshal.GetLastWin32Error()));
                    originalTimeRestored = true;
                }
                if (!originalDurable) { RandomAccess.FlushToDisk(handle); originalDurable = true; }
            }
            Close();
        }
    }
    private static readonly List<WriteReceipt> failedWrites = [];
    internal static bool HasFailedWrites { get { lock (failedWrites) return failedWrites.Count != 0; } }
    internal static void RetryFailedWrites()
    {
        lock (failedWrites)
        {
            List<Exception>? errors = null;
            for (var index = failedWrites.Count - 1; index >= 0; index--)
            {
                try { failedWrites[index].Recover(); failedWrites.RemoveAt(index); }
                catch (Exception error) { (errors ??= []).Add(error); }
            }
            if (errors != null) throw new AggregateException("The owned file save requires recovery.", errors);
        }
    }
    internal static OwnedFileWriteResult Write(string path, byte[]? expectedHash, byte[] contents)
        => Write(path, expectedHash, contents, null);
    internal static OwnedFileWriteResult Write(string path, byte[]? expectedHash, byte[] contents, OwnedFileIdentity? expectedIdentity)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        // Snapshot caller data and its hash before native creation/mutation.
        var written = contents.ToArray();
        var writtenHash = SHA256.HashData(written);
        var expected = expectedHash?.ToArray();
        const uint read = 0x80000000, write = 0x40000000, delete = 0x00010000, shareRead = 1, createNew = 1, openExisting = 3;
        path = Path.GetFullPath(path);
        var access = read | write | (expected == null ? delete : 0);
        var handle = CreateFileW(path, access, shareRead, 0, expected == null ? createNew : openExisting, 0x80 | 0x00200000, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error(); handle.Dispose();
            throw new IOException("The authored file could not be opened for saving.", new Win32Exception(error));
        }
        if (expectedIdentity != null)
        {
            try
            {
                var information = Information(handle);
                if ((information.Attributes & (0x10 | 0x400)) != 0
                    || new OwnedFileIdentity(information.Volume, information.IndexHigh, information.IndexLow) != expectedIdentity.Value)
                    throw new IOException("The authored file was replaced outside the editor.");
            }
            catch { handle.Dispose(); throw; }
        }
        return CompleteWrite(handle, expected, written, writtenHash);
    }
    internal static OwnedFileWriteResult WriteNewInDirectory(string path, OwnedDirectoryIdentity parentIdentity, byte[] contents)
    {
        var written = contents.ToArray(); var hash = SHA256.HashData(written);
        var handle = WindowsOwnedDirectory.CreateNewFile(Path.GetFullPath(path), parentIdentity);
        return CompleteWrite(handle, null, written, hash);
    }
    internal static OwnedFileIdentity CaptureIdentity(FileStream stream)
    {
        var info = Information(stream.SafeFileHandle);
        if ((info.Attributes & (0x10 | 0x400)) != 0) throw new IOException("The publication file is unavailable or redirected.");
        return new(info.Volume, info.IndexHigh, info.IndexLow);
    }
    private static OwnedFileWriteResult CompleteWrite(SafeFileHandle handle, byte[]? expected, byte[] written, byte[] writtenHash)
    {
        var receipt = new WriteReceipt(handle, expected == null, written, writtenHash);
        try
        {
            var result = receipt.Apply(expected);
            // All fallible work finishes before a canonical success receipt returns.
            receipt.Close();
            return result;
        }
        catch (Exception failure)
        {
            lock (failedWrites)
            {
                failedWrites.Add(receipt);
                try { receipt.Recover(); failedWrites.Remove(receipt); }
                catch (Exception recovery) { throw new AggregateException("The authored file save could not be restored.", failure, recovery); }
            }
            throw;
        }
    }
    private static FileInfo Information(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw new IOException("The owned authored file information is unavailable.", new Win32Exception(Marshal.GetLastWin32Error()));
        return info;
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileDisposition { public byte DeleteFile; }
    [StructLayout(LayoutKind.Sequential)] private struct FileTime { public uint Low, High; }
    [StructLayout(LayoutKind.Sequential)] private struct FileInfo
    {
        public uint Attributes, CreatedLow, CreatedHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref FileDisposition information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfo information);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileTime(SafeFileHandle file, nint creation, nint access, ref FileTime write);
}
