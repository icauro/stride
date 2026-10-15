using System.Security.Cryptography;

namespace Stride.Editor.ImGui;

internal partial class ScriptSourceDocument
{
    internal IDisposable? AcquireSourceTransferLease()
    {
        if (SessionHistory == null) return null;
        var owner = SessionHistory.Registry.Find(Path);
        if (owner == null || !owner.IsPublished || !ReferenceEquals(owner.Document, this))
            throw new InvalidOperationException("The source no longer has its current canonical owner.");
        return owner.AcquireView();
    }
    internal void ValidateSourceTransfer(string expectedPath, long expectedVersion, string expectedText, byte[]? expectedHash, bool editing)
    {
        if (Path != expectedPath || Version != expectedVersion || Text != expectedText || editing && !CanEditText)
            throw new InvalidOperationException("The source changed during the native text transfer.");
        if (SessionHistory != null && !ReferenceEquals(SessionHistory.Registry.Find(Path)?.Document, this))
            throw new InvalidOperationException("The source no longer has its current canonical owner.");
        if (Directory.Exists(Path)) throw new InvalidOperationException("The source path is no longer a file.");
        if (!File.Exists(Path))
        {
            if (expectedHash != null) throw new InvalidOperationException("The source file was removed during the native text transfer.");
            return;
        }
        if (editing && (File.GetAttributes(Path) & FileAttributes.ReadOnly) != 0)
            throw new InvalidOperationException("This source file is read-only.");
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (expectedHash == null || !SHA256.HashData(stream).AsSpan().SequenceEqual(expectedHash))
            throw new InvalidOperationException("The source file changed outside the editor.");
    }
}
