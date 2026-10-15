using System.Security.Cryptography;
using Stride.Core.Assets;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class AssetCreationOperation(EditorGame game, Package package, AssetItem item, EditorAuthoringState state,
        PackageMetadataRevision[] metadata) : Operation
    {
        private bool applied, detached;
        private readonly bool initialPackageDirty = package.IsDirty;
        private byte[]? savedBytes;
        private IAssetReferenceDocument Document => (IAssetReferenceDocument)state.Document;
        private IRelocatableEditorDocument FileDocument => (IRelocatableEditorDocument)state.Document;
        public void ApplyInitial() => Add(false);
        public void CancelInitial() => Remove(true);
        protected override void Rollback() => Remove(false);
        protected override void Rollforward() => Add(true);
        private void Add(bool restoring)
        {
            var path = state.Document.Path;
            if (applied) throw new InvalidOperationException("The created asset is already published.");
            if (!game.IsCurrentEditableAssetPackage(package)) throw new InvalidOperationException("The asset creation package is no longer available.");
            if (package.IsReadOnly || package.Assets.Find(item.Id) != null || package.Assets.Any(asset => string.Equals(asset.Location, item.Location, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The asset creation destination is occupied.");
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("The asset creation path is occupied by another file.");
            var wasDirty = package.IsDirty;
            var added = false; var published = false; var restoredFile = false;
            try
            {
                if (restoring && savedBytes != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    WindowsOwnedAssetFileWriter.Write(path, null, savedBytes); restoredFile = true;
                }
                item.IsDeleted = false; package.Assets.Add(item); added = true;
                if (restoring && detached) { game.SetDeletedAssetDocuments([Document], false); detached = false; }
                else game.authoring!.Publish(state);
                published = true;
                foreach (var change in metadata) change.Apply(true);
                item.IsDirty = true; package.IsDirty = true; game.workspace!.RefreshAssets(); applied = true;
            }
            catch (Exception failure)
            {
                var recovery = new List<Exception>();
                void Recover(Action action) { try { action(); } catch (Exception error) { recovery.Add(error); } }
                if (published) Recover(() => { if (restoring) { game.SetDeletedAssetDocuments([Document], true); detached = true; } else game.authoring!.Unpublish(state); });
                if (added) Recover(() => package.Assets.RemoveById(item.Id));
                foreach (var change in metadata) Recover(() => change.Apply(false));
                if (restoredFile) Recover(() => WindowsOwnedAssetFileDeletion.Delete(path, SHA256.HashData(savedBytes!)));
                package.IsDirty = wasDirty; Recover(() => game.workspace!.RefreshAssets());
                if (recovery.Count != 0) { recovery.Insert(0, failure); throw new AggregateException("Asset creation and restoration failed.", recovery); }
                throw;
            }
        }
        private void Remove(bool cancelling)
        {
            if (!applied) return;
            if (!ReferenceEquals(package.Assets.Find(item.Id), item) || !state.IsPublished || !ReferenceEquals(game.authoring!.Find(item.Id), state))
                throw new InvalidOperationException("The created asset no longer has its authored ownership.");
            var path = state.Document.Path;
            byte[]? current = null;
            if (File.Exists(path))
            {
                current = File.ReadAllBytes(path);
                if (!SHA256.HashData(current).AsSpan().SequenceEqual(FileDocument.DiskContentHash.Span))
                    throw new IOException("The created asset file changed outside the editor.");
            }
            else if (Directory.Exists(path) || !FileDocument.DiskContentHash.IsEmpty)
                throw new IOException("The created asset file changed outside the editor.");
            var wasDirty = package.IsDirty;
            var removedFile = false; var removed = false; var unpublished = false;
            try
            {
                if (current != null) { WindowsOwnedAssetFileDeletion.Delete(path, FileDocument.DiskContentHash.Span); removedFile = true; }
                if (!package.Assets.RemoveById(item.Id)) throw new InvalidOperationException("The created asset no longer exists.");
                removed = true; item.IsDeleted = true;
                if (cancelling) game.authoring!.Unpublish(state);
                else { game.SetDeletedAssetDocuments([Document], true); detached = true; }
                unpublished = true;
                foreach (var change in metadata) change.Apply(false);
                package.IsDirty = cancelling ? initialPackageDirty : true;
                savedBytes = current; game.workspace!.RefreshAssets(); applied = false;
            }
            catch (Exception failure)
            {
                var recovery = new List<Exception>();
                void Recover(Action action) { try { action(); } catch (Exception error) { recovery.Add(error); } }
                if (removed) Recover(() => { item.IsDeleted = false; package.Assets.Add(item); });
                if (unpublished) Recover(() => { if (cancelling) game.authoring!.Publish(state); else { game.SetDeletedAssetDocuments([Document], false); detached = false; } });
                foreach (var change in metadata) Recover(() => change.Apply(true));
                if (removedFile) Recover(() => WindowsOwnedAssetFileWriter.Write(path, null, current!));
                package.IsDirty = wasDirty; Recover(() => game.workspace!.RefreshAssets());
                if (recovery.Count != 0) { recovery.Insert(0, failure); throw new AggregateException("Asset creation undo and restoration failed.", recovery); }
                throw;
            }
        }
        protected override void FreezeContent()
        {
            if (detached) game.ForgetDeletedAssetDocuments([Document]);
            savedBytes = null;
        }
    }
}
