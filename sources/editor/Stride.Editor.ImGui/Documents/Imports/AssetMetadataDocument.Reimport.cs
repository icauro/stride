using Stride.Assets.Models;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets.Tracking;
using Stride.Core.IO;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Core.Storage;
using Stride.Core.Transactions;
using Stride.SpriteStudio.Assets;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal void ApplySourceReimport(Asset? imported, IReadOnlyDictionary<UFile, ObjectId> hashes)
    {
        CommitEdits();
        if (!CanSave || historyScope.AuthoringState is not { IsPublished: true } owner
            || !ReferenceEquals(SessionHistory?.Registry.Find(AuthoredAsset.Id), owner))
            throw new InvalidOperationException("The source asset is no longer editable.");
        if (imported != null && imported.GetType() != AuthoredAsset.GetType()) throw new InvalidDataException("The imported source type changed.");
        var root = AuthoringGraph.RootNode;
        void RequireMember(string name)
        {
            if (!CanEditAssetSettingNode(root[name])) throw new InvalidOperationException("The source setting is sealed.");
        }
        IAssetObjectNode RequireCollection(string name)
        {
            var collection = (IAssetObjectNode)root[name].Target;
            if (!CanEditAssetSettingNode(collection) || collection.Indices.Any(index => !CanEditAssetSettingItem(collection, index)))
                throw new InvalidOperationException("The source collection contains sealed items.");
            return collection;
        }
        switch (AuthoredAsset, imported)
        {
            case (ModelAsset current, ModelAsset incoming):
            {
                var collection = RequireCollection(nameof(ModelAsset.Materials));
                var materials = incoming.Materials.ToDictionary(material => material.Name, StringComparer.Ordinal);
                foreach (var material in current.Materials) materials[material.Name] = material;
                var ids = CollectionItemIdHelper.GetCollectionItemIds(current.Materials)
                    .ToDictionary(entry => current.Materials[(int)entry.Key].Name, entry => entry.Value, StringComparer.Ordinal);
                ApplyInheritedAssetMutation(() =>
                {
                    while (current.Materials.Count > 0) collection.Remove(current.Materials[0], new NodeIndex(0));
                    for (var index = 0; index < incoming.Materials.Count; index++)
                    {
                        var name = incoming.Materials[index].Name;
                        collection.Restore(materials[name], new NodeIndex(index), ids.TryGetValue(name, out var id) ? id : ItemId.New());
                    }
                }, "Update asset from source");
                break;
            }
            case (SkeletonAsset current, SkeletonAsset incoming):
            {
                var collection = RequireCollection(nameof(SkeletonAsset.Nodes));
                var preserve = current.Nodes.ToDictionary(node => node.Name, node => node.Preserve, StringComparer.Ordinal);
                var ids = CollectionItemIdHelper.GetCollectionItemIds(current.Nodes)
                    .ToDictionary(entry => current.Nodes[(int)entry.Key].Name, entry => entry.Value, StringComparer.Ordinal);
                ApplyInheritedAssetMutation(() =>
                {
                    while (current.Nodes.Count > 0) collection.Remove(current.Nodes[0], new NodeIndex(0));
                    for (var index = 0; index < incoming.Nodes.Count; index++)
                    {
                        var node = incoming.Nodes[index];
                        if (preserve.TryGetValue(node.Name, out var retained)) node.Preserve = retained;
                        collection.Restore(node, new NodeIndex(index), ids.TryGetValue(node.Name, out var id) ? id : ItemId.New());
                    }
                }, "Update asset from source");
                break;
            }
            case (AnimationAsset, AnimationAsset incoming):
                RequireMember(nameof(AnimationAsset.AnimationTimeMinimum)); RequireMember(nameof(AnimationAsset.AnimationTimeMaximum));
                ApplyInheritedAssetMutation(() =>
                {
                    root[nameof(AnimationAsset.AnimationTimeMinimum)].Update(incoming.AnimationTimeMinimum);
                    root[nameof(AnimationAsset.AnimationTimeMaximum)].Update(incoming.AnimationTimeMaximum);
                }, "Update asset from source");
                break;
            case (SpriteStudioModelAsset, SpriteStudioModelAsset incoming):
                RequireMember(nameof(SpriteStudioModelAsset.NodeNames));
                ApplyInheritedAssetMutation(() => root[nameof(SpriteStudioModelAsset.NodeNames)].Update(incoming.NodeNames), "Update asset from source");
                break;
            case (_, null): break;
            default: throw new InvalidOperationException("This asset has no source merge policy.");
        }
        SourceHashesHelper.ConvertPaths(AuthoredAsset, new UDirectory(System.IO.Path.GetDirectoryName(Path)!), UPathType.Absolute);
        var before = SourceHashesHelper.GetAllHashes(AuthoredAsset);
        SourceHashesHelper.ConvertPaths(AuthoredAsset, new UDirectory(System.IO.Path.GetDirectoryName(Path)!), UPathType.Relative);
        var after = new Dictionary<UFile, ObjectId>(hashes);
        if (before.Count == after.Count && before.All(entry => after.TryGetValue(entry.Key, out var value) && value == entry.Value)) return;
        var beforeSnapshot = SourceHashesHelper.CaptureSnapshot(AuthoredAsset);
        var prepared = AssetCloner.Clone(AuthoredAsset);
        SourceHashesHelper.UpdateHashes(prepared, after);
        SourceHashesHelper.ConvertPaths(prepared, new UDirectory(System.IO.Path.GetDirectoryName(Path)!), UPathType.Relative);
        var operation = new ReimportHashesOperation(this, beforeSnapshot, SourceHashesHelper.CaptureSnapshot(prepared), revision, ++nextRevision);
        operation.ApplyInitial();
        try { using var transaction = CreateDocumentTransaction("Update asset from source"); RecordOperation(operation); }
        catch { operation.RestoreInitial(); throw; }
    }

    private sealed class ReimportHashesOperation(AssetMetadataDocument document, SourceHashesHelper.Snapshot before,
        SourceHashesHelper.Snapshot after, long beforeRevision, long afterRevision) : Operation
    {
        public void ApplyInitial() => Apply(after, afterRevision);
        public void RestoreInitial() => Apply(before, beforeRevision);
        protected override void Rollback() => Apply(before, beforeRevision);
        protected override void Rollforward() => Apply(after, afterRevision);
        private void Apply(SourceHashesHelper.Snapshot hashes, long revision)
        {
            SourceHashesHelper.RestoreSnapshot(document.AuthoredAsset, hashes);
            document.revision = revision; document.ImportSettingsEditVersion++;
        }
    }
}
