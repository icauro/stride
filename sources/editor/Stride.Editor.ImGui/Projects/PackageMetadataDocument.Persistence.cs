using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class PackageMetadataDocument
{
    private sealed record NativeWriteReceipt(EditorAuthoringState Owner, Guid Generation, long Revision, string Path, byte[] Hash);
    private NativeWriteReceipt? nativeWriteReceipt;
    internal void WriteNativeMetadata(Package source, byte[] bytes)
    {
        if (!ReferenceEquals(package, source) || !CanSave || !workspace.Session.Packages.Contains(package)
            || !ReferenceEquals(package.Session, workspace.Session) || package.State < PackageState.AssetsReady)
            throw new InvalidOperationException("The package metadata owner is no longer available.");
        var state = SessionHistory?.Registry.Find(Path);
        if (state is not { IsPublished: true, IsDisposed: false } || !ReferenceEquals(state.Document, this))
            throw new InvalidOperationException("The package metadata has no current canonical owner.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var written = WindowsOwnedAssetFileWriter.Write(Path, recoveryDiskBaseline.Length == 0 ? null : recoveryDiskBaseline, bytes);
        // A later removal/project failure must retry against this actual successful write,
        // while revision/save boundaries stay dirty until the whole native save completes.
        recoveryDiskBaseline = written.ContentHash;
        nativeWriteReceipt = new(state, state.Generation, revision, Path, written.ContentHash.ToArray());
    }
    internal void NotifyNativeSessionSaved()
    {
        var receipt = nativeWriteReceipt;
        if (receipt == null || package.IsDirty || !CanSave || package.State < PackageState.AssetsReady
            || !ReferenceEquals(package.Session, workspace.Session) || !workspace.Session.Packages.Contains(package)) return;
        var current = SessionHistory?.Registry.Find(Path);
        if (!ReferenceEquals(current, receipt.Owner) || current is not { IsPublished: true, IsDisposed: false }
            || current.Generation != receipt.Generation || revision != receipt.Revision
            || !string.Equals(Path, receipt.Path, StringComparison.OrdinalIgnoreCase)
            || !recoveryDiskBaseline.AsSpan().SequenceEqual(receipt.Hash)
            || !package.TryGetMetadataDiskBaseline(Path, out var nativeHash)
            || nativeHash == null || !nativeHash.AsSpan().SequenceEqual(receipt.Hash)) return;
        // A previous partial save receipt survives until the full native session
        // save succeeds. Filtered/removed owners without a write are never saved.
        savedRevision = revision; savedMetadata = CaptureMetadata(); initiallyDirty = false;
        RememberRecoveryDiskBaseline(); nativeWriteReceipt = null;
    }
}

internal sealed partial class EditorGame
{
    private void SaveOwnedPackageMetadata(Package package, byte[] bytes)
    {
        if (!IsCurrentEditableAssetPackage(package) || authoring == null || sessionHistory == null || workspace == null)
            throw new InvalidOperationException("The package cannot be saved in the current workspace.");
        var state = authoring.GetOrCreate(package.FullPath.ToOSPath(), null,
            () => new PackageMetadataDocument(workspace, package, sessionHistory));
        using var lease = state.AcquireView();
        state.Document.WriteNativeMetadata(package, bytes);
    }
}
