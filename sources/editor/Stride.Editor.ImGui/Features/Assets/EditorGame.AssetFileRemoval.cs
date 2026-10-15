using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void RemoveOwnedAssetFile(Package package, string path, PackageFileRemovalKind kind)
    {
        path = Path.GetFullPath(path);
        if (package.IsReadOnly) throw new IOException("This package can't be edited.");
        if (kind == PackageFileRemovalKind.RetainedAsset)
        {
            if (workspace == null || !workspace.Session.Packages.Contains(package) || !ReferenceEquals(package.Session, workspace.Session))
                throw new IOException("The retained asset is no longer owned by this workspace.");
            var current = package.Assets.SingleOrDefault(asset => string.Equals(Path.GetFullPath(asset.FullPath.ToOSPath()), path, StringComparison.OrdinalIgnoreCase));
            var owner = current == null ? null : authoring?.Find(current.Id);
            if (owner is not { IsPublished: true, IsDisposed: false, Document: IRelocatableEditorDocument document }
                || !string.Equals(owner.CanonicalPath, path, StringComparison.OrdinalIgnoreCase)
                || document.DiskContentHash.IsEmpty || !File.Exists(path)
                || !System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(document.DiskContentHash.Span))
                throw new IOException("The asset reusing an obsolete path has no current owned saved-file receipt. Reload the project before saving.");
            return;
        }
        if (kind == PackageFileRemovalKind.Obsolete && package.TryGetObsoleteMetadataDiskBaseline(path, out var metadataHash))
        {
            if (workspace == null || !workspace.Session.Packages.Contains(package) || !ReferenceEquals(package.Session, workspace.Session))
                throw new IOException("The moved package is no longer owned by this workspace.");
            WindowsOwnedAssetFileDeletion.Delete(path, metadataHash!);
            package.AcknowledgeObsoleteMetadataRemoval(path);
            return;
        }
        if (kind == PackageFileRemovalKind.Generated && visualScriptOutputRemovals.TryGetValue(path, out var generatedRemoval))
        { generatedRemoval.RemoveSavedFile(package); return; }
        if (TryGuardScriptSourceFileRemoval(package, path, kind)) return;
        if (assetDeletionHistory.TryRemoveOwnedFile(path)) return;
        if (assetRelocationHistory.TryRemoveMovedFromFile(path)) return;
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        // Explicit generated build artifacts are outside authored asset ownership.
        // Other old/queued paths require an operation-owned fingerprint or move
        // tombstone; their extension alone cannot establish ownership.
        if (kind == PackageFileRemovalKind.Generated) { File.Delete(path); return; }
        throw new IOException("The obsolete asset file is not owned by this operation. Reload the project before saving.");
    }
}
