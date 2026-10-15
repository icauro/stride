using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.IO;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool CanCreatePrefabFromSelection => document != null && CanCreatePrefabFromHierarchySelection(document);
    private bool CanCreatePrefabFromHierarchySelection(EntityHierarchyDocument source) => workspace != null && authoring != null
        && sessionHistory != null && SelectedHierarchyDocuments.Count > 0 && SelectedHierarchyDocuments.Contains(source)
        && SelectedHierarchyDocuments.All(owner => owner.CanExtractPrefab && ReferenceEquals(owner.SessionHistory, sessionHistory)
            && ReferenceEquals(authoring.Find(owner.Asset.Id)?.Document, owner) && !workspace.FindAsset(owner.Asset.Id).Package.IsReadOnly);

    private void CreatePrefabFromSelection(EntityHierarchyDocument source)
    {
        if (source != document || !CanCreatePrefabFromSelection) throw new InvalidOperationException("A prefab cannot be created from this selection.");
        ValidatePendingPropertyStrings();
        documents.CommitAll();
        var sourceItem = workspace!.FindAsset(source.Asset.Id);
        var package = sourceItem.Package;
        var owners = SelectedHierarchyDocuments.ToArray();
        // The active source determines the new asset's package/location; every selected owner contributes real parts.
        var extractions = EntityHierarchyDocument.PreparePrefabExtractions(owners);
        var extraction = extractions[0];
        var sourceUrl = sourceItem.UnqualifiedUrl.ToString().Replace('\\', '/');
        var slash = sourceUrl.LastIndexOf('/');
        var directory = slash >= 0 ? sourceUrl[..(slash + 1)] : "";
        // Same sanitization and NamingHelper variation pattern used by the WPF extraction command.
        var sanitized = Regex.Replace(extraction.SuggestedName, "[=;,/\\\\?:&!.*<>|#%\"]", "_");
        if (string.IsNullOrWhiteSpace(sanitized)) sanitized = "Prefab";
        string AssetPath(string relative)
        {
            var qualified = package.Container?.Qualify(new UFile(relative)) ?? new UFile(relative);
            // AssetItem resolves the actual configured package source folder and namespace.
            var candidate = sourceItem.Clone(true, qualified, extraction.Prefab);
            candidate.AlternativePath = null;
            return candidate.FullPath.ToOSPath();
        }
        bool Occupied(UFile name)
        {
            var url = directory + name;
            return package.Assets.Any(item => item.UnqualifiedUrl.ToString().Equals(url, StringComparison.OrdinalIgnoreCase))
                || File.Exists(AssetPath(url)) || Directory.Exists(AssetPath(url));
        }
        var name = NamingHelper.ComputeNewName(sanitized, Occupied);
        var url = directory + name;
        var location = package.Container?.Qualify(new UFile(url)) ?? new UFile(url);
        var path = AssetPath(url);
        var item = new AssetItem(location, extraction.Prefab) { SourceFolder = sourceItem.SourceFolder };
        extraction.Metadata.CopyInto(item.YamlMetadata);
        var prefab = new PrefabDocument(path, extraction.Prefab, extraction.Metadata, sessionHistory!);
        EditorAuthoringState<PrefabDocument>? prefabState = null;
        try
        {
            prefabState = authoring!.RegisterUnpublished(prefab, item.Id);
            using var retention = prefabState.AcquireView();
            _ = prefab.HierarchyGraph;
            var sourceStates = owners.Select(owner => authoring.Find(owner.Asset.Id)
                ?? throw new InvalidOperationException("The selected scene has no canonical authored graph.")).ToArray();
            var metadata = PackageMetadataRevision.Prepare(sessionHistory, workspace, [package]);
            var operation = new PrefabCreationOperation(this, extractions, package, item, prefabState, metadata);
            var participants = sourceStates.Append(prefabState).Concat(metadata.Select(change => change.State)).Distinct().ToArray();
            using var capture = sessionHistory!.CaptureOperationsAtomic(() =>
            {
                operation.ApplyInitial();
                try { sessionHistory.PushOperation(operation, participants); }
                catch (Exception failure)
                {
                    try { sessionHistory.CompensateInitialMutation(operation.CancelInitial); }
                    catch (Exception recovery)
                    {
                        sessionHistory.RetainFailedInitialOperation(operation, participants);
                        throw new AggregateException("Prefab publication and restoration failed.", failure, recovery);
                    }
                    throw;
                }
                foreach (var ownerExtraction in extractions) ownerExtraction.RelinkWithJournal(item);
            });
            capture.Publish(sessionHistory, "Create " + name + " from selection");
            assetDeletionCommandContext = assetRelocationCommandContext = packageFolderCommandContext = assetTagBatchCommandContext = assetCommandContext = false;
            if (assetBrowser.RevealAsset(workspace, item.Id) is { } entry) selectedAssetPath = entry.Path;
            engineViewport?.Picker?.Cancel("Create prefab from selection");
        }
        catch
        {
            if (prefabState == null) prefab.Dispose();
            else if (!prefabState.IsPublished) authoring!.ReleaseIfUnused(prefabState);
            throw;
        }
    }

    private sealed class PrefabCreationOperation(EditorGame game, EntityHierarchyDocument.PreparedPrefabExtraction[] extractions,
        Package package, AssetItem item, EditorAuthoringState<PrefabDocument> state, PackageMetadataRevision[] metadata) : Operation
    {
        private bool applied;
        private bool detached;
        private byte[]? savedBytes;
        private readonly bool initialPackageDirty = package.IsDirty;
        internal void ApplyInitial() => Insert(false);
        internal void CancelInitial() => Remove(true);
        protected override void Rollback() => Remove(false);
        protected override void Rollforward() => Insert(true);

        private void Insert(bool restoring)
        {
            if (package.IsReadOnly || package.Assets.Find(item.Id) != null
                || package.Assets.Any(asset => asset.Location == item.Location)) throw new InvalidOperationException("The prefab path is occupied by another asset.");
            var wasDirty = package.IsDirty;
            var added = false; var published = false; var fileCreated = false;
            try
            {
                if (restoring) fileCreated = RestoreOwnedFile();
                item.IsDeleted = false; package.Assets.Add(item); added = true;
                if (!string.Equals(System.IO.Path.GetFullPath(item.FullPath.ToOSPath()), state.CanonicalPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The created prefab path changed with its package source folder.");
                if (restoring && detached) { game.SetDeletedAssetDocuments([state.Document], false); detached = false; }
                else game.authoring!.Publish(state);
                published = true;
                foreach (var extraction in extractions)
                    game.sessionHistory!.HierarchyGraphs.RegisterPreparedBase(extraction.Source, state.Document);
                foreach (var change in metadata) change.Apply(true);
                package.IsDirty = true; item.IsDirty = true;
                game.workspace!.RefreshAssets(); applied = true;
            }
            catch (Exception failure)
            {
                var recovery = new List<Exception>();
                void Recover(Action action) { try { action(); } catch (Exception error) { recovery.Add(error); } }
                foreach (var extraction in extractions) Recover(() => game.sessionHistory!.HierarchyGraphs.Refresh(extraction.Source));
                if (published) Recover(() =>
                {
                    if (restoring) { game.SetDeletedAssetDocuments([state.Document], true); detached = true; }
                    else game.authoring!.Unpublish(state);
                });
                if (added) Recover(() => package.Assets.RemoveById(item.Id));
                foreach (var change in metadata) Recover(() => change.Apply(false));
                if (fileCreated) Recover(() => RemoveOwnedFile());
                package.IsDirty = wasDirty; Recover(() => game.workspace!.RefreshAssets());
                if (recovery.Count > 0) { recovery.Insert(0, failure); throw new AggregateException("Prefab creation and restoration failed.", recovery); }
                throw;
            }
        }
        private void Remove(bool cancel)
        {
            if (!applied) return;
            if (!ReferenceEquals(package.Assets.Find(item.Id), item) || !state.IsPublished)
                throw new InvalidOperationException("The created prefab no longer has its authored ownership.");
            var wasDirty = package.IsDirty;
            var removed = false; var unpublished = false; var fileRemoved = false;
            try
            {
                fileRemoved = RemoveOwnedFile();
                foreach (var extraction in extractions) game.sessionHistory!.HierarchyGraphs.Refresh(extraction.Source);
                if (!package.Assets.RemoveById(item.Id)) throw new InvalidOperationException("The created prefab no longer exists.");
                removed = true; item.IsDeleted = true;
                if (cancel) game.authoring!.Unpublish(state);
                else { game.SetDeletedAssetDocuments([state.Document], true); detached = true; }
                unpublished = true;
                foreach (var change in metadata) change.Apply(false);
                package.IsDirty = cancel ? initialPackageDirty : true; game.workspace!.RefreshAssets(); applied = false;
            }
            catch (Exception failure)
            {
                var recovery = new List<Exception>();
                void Recover(Action action) { try { action(); } catch (Exception error) { recovery.Add(error); } }
                if (removed) Recover(() => { item.IsDeleted = false; package.Assets.Add(item); });
                if (unpublished) Recover(() =>
                {
                    if (cancel) game.authoring!.Publish(state);
                    else { game.SetDeletedAssetDocuments([state.Document], false); detached = false; }
                });
                foreach (var extraction in extractions) Recover(() => game.sessionHistory!.HierarchyGraphs.RegisterPreparedBase(extraction.Source, state.Document));
                foreach (var change in metadata) Recover(() => change.Apply(true));
                if (fileRemoved) Recover(() => RestoreOwnedFile());
                package.IsDirty = wasDirty; Recover(() => game.workspace!.RefreshAssets());
                if (recovery.Count > 0) { recovery.Insert(0, failure); throw new AggregateException("Prefab removal and restoration failed.", recovery); }
                throw;
            }
        }
        private bool RemoveOwnedFile()
        {
            var path = state.Document.Path;
            if (!File.Exists(path))
            {
                if (Directory.Exists(path) || !state.Document.DiskContentHash.IsEmpty)
                    throw new IOException("The created prefab file changed outside the editor.");
                return false;
            }
            var bytes = File.ReadAllBytes(path);
            if (!SHA256.HashData(bytes).AsSpan().SequenceEqual(state.Document.DiskContentHash.Span))
                throw new IOException("The created prefab file changed outside the editor.");
            WindowsOwnedAssetFileDeletion.Delete(path, state.Document.DiskContentHash.Span);
            savedBytes = bytes; return true;
        }
        private bool RestoreOwnedFile()
        {
            var path = state.Document.Path;
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("The created prefab path is occupied by another file.");
            if (savedBytes == null) return false;
            WindowsOwnedAssetFileWriter.Write(path, null, savedBytes); return true;
        }
        protected override void FreezeContent()
        {
            if (detached) game.ForgetDeletedAssetDocuments([state.Document]);
            savedBytes = null;
        }
    }
}
