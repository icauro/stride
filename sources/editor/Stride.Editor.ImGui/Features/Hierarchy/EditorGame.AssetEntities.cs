using Stride.Assets.Entities;
using Stride.Core.Assets;
using Stride.Core.Mathematics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool CanDropAssets(AssetId[] ids, EntityHierarchyDocument target, Guid? parent, string folder, int index, out string reason)
    {
        reason = "The assets cannot be added here.";
        if (workspace == null || sessionHistory == null || authoring == null || ids.Length == 0 || IsAuthoringPipelineUpdating
            || !IsCurrentHierarchyContextOwner(target)
            || !ReferenceEquals(target.SessionHistory, sessionHistory) || !ReferenceEquals(authoring.Find(target.Asset.Id)?.Document, target)) return false;
        foreach (var id in ids)
        {
            var item = workspace.AssetItems.FirstOrDefault(item => item.Id == id);
            if (item == null) return false;
            if (item.Asset is SceneAsset)
            { if (!CanDropScene(id, target, parent, folder, index, out reason)) return false; }
            else if (item.Asset is PrefabAsset)
            { if (!CanInstantiatePrefab(id, target, parent, folder, index, out reason)) return false; }
            else
            {
                if (!target.CanInsertAssetEntities(parent, folder, index)) return false;
                if (!CanCreateDroppedComponent(item, out _, out reason)) return false;
            }
        }
        return true;
    }
    private void DropAssetsToHierarchy(AssetId[] ids, EntityHierarchyDocument target, Guid? parent, string folder, int index,
        bool createContainer, Vector3? worldPosition, bool viewportDrop)
    {
        if (!CanDropAssets(ids, target, parent, folder, index, out var reason)) throw new InvalidOperationException(reason);
        ValidatePendingPropertyStrings();
        FinishEntityRename(true); documents.CommitAll();
        if (!CanDropAssets(ids, target, parent, folder, index, out reason)) throw new InvalidOperationException(reason);
        void ClearDropSelection()
        {
            SelectionService.ClearPresentedHierarchies();
            selectedHierarchyFolder = null; folderSelectionDocument = null;
        }
        if (!viewportDrop) ClearDropSelection();
        var components = new Dictionary<AssetId, (Stride.Engine.Entity Entity, EditorAuthoringState Owner)>();
        var pendingResources = new Dictionary<AssetId, List<IDisposable>>();
        try
        {
            foreach (var id in ids)
            {
                var item = workspace!.FindAsset(id);
                if (item.Asset is PrefabAsset or SceneAsset) continue;
                var entry = new AssetEntry(item.FullPath.ToOSPath(), item.UnqualifiedUrl.ToString(), System.IO.Path.GetExtension(item.FullPath.ToOSPath()),
                    item.Asset.GetType(), item.Id, item.Asset.Tags.ToArray());
                var owner = GetAssetTagDocument(entry, false);
                var state = authoring!.Find(id) ?? throw new InvalidOperationException("The source asset has no canonical authored owner.");
                if (!ReferenceEquals(state.Document, owner)) throw new InvalidOperationException("The source asset authored generation changed.");
                var resources = new List<IDisposable>(); pendingResources.Add(id, resources);
                var component = CreateDroppedComponent(item.Clone(true, newAsset: owner.AuthoredAsset), resources);
                var entity = new Stride.Engine.Entity(System.IO.Path.GetFileName(item.UnqualifiedUrl.ToString())); entity.Add(component);
                components.Add(id, (entity, state));
            }
            var scenePosition = worldPosition is { } point ? point - GetSceneHierarchyOffset(target) : (Vector3?)null;
            using var transaction = sessionHistory!.CreateTransaction("Add assets to hierarchy");
            using var captured = sessionHistory.CaptureOperationsAtomic(() =>
            {
                var inserted = new List<Guid>();
                var scenes = ids.Where(id => workspace!.FindAsset(id).Asset is SceneAsset).ToArray();
                if (scenes.Length > 0)
                {
                    var insertedScenes = ReparentDroppedScenes(scenes, (SceneDocument)target, index, scenePosition ?? Vector3.Zero);
                    foreach (var scene in insertedScenes) RequestSceneLoading(scene, true, false);
                }
                var insertion = index == -1 ? parent is { } owner ? target.Asset.Hierarchy.Parts[owner].Entity.Transform.Children.Count
                    : target.Asset.Hierarchy.RootParts.Count : index;
                foreach (var id in ids)
                {
                    if (workspace!.FindAsset(id).Asset is SceneAsset) continue;
                    if (workspace.FindAsset(id).Asset is PrefabAsset)
                    {
                        var result = InstantiatePrefab(id, target, parent, folder, insertion, createContainer, worldPosition);
                        inserted.AddRange(result.Roots.Select(entity => entity.Id)); insertion += result.Roots.Count;
                    }
                    else
                    {
                        var source = components[id];
                        var resources = pendingResources[id].ToArray(); pendingResources.Remove(id);
                        target.InsertAssetEntitiesWithResources([source.Entity], parent, folder, insertion++, scenePosition, resources, source.Owner);
                        inserted.Add(source.Entity.Id);
                    }
                }
                ClearDropSelection();
                target.RecordImportedAssetSelection(viewportDrop ? inserted : []);
                if (viewportDrop) SelectionService.SetHierarchyContent(inserted.Select(id => HierarchyContentItem.Entity(target, id)));
            });
            captured.Publish(sessionHistory, "Add assets to hierarchy");
            assetDeletionCommandContext = assetRelocationCommandContext = packageFolderCommandContext = assetTagBatchCommandContext = assetCommandContext = false;
        }
        finally { foreach (var resource in pendingResources.Values.SelectMany(values => values)) resource.Dispose(); }
    }
}
