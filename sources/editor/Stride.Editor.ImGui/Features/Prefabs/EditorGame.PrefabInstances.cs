using Stride.Assets.Entities;
using Stride.Core.Assets;
using Stride.Core.Mathematics;
using Stride.Core.Serialization;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool CanInstantiatePrefab(AssetId prefabId, EntityHierarchyDocument target, Guid? parentId, string folder, int index, out string reason)
    {
        reason = "The prefab cannot be added here.";
        if (workspace == null || sessionHistory == null || authoring == null || !ReferenceEquals(target.SessionHistory, sessionHistory)
            || !target.CanInsertPrefab(parentId, folder, index) || !ReferenceEquals(authoring.Find(target.Asset.Id)?.Document, target)) return false;
        var item = workspace.AssetItems.FirstOrDefault(item => item.Id == prefabId);
        if (item?.Asset is not PrefabAsset stored) return false;
        var owner = authoring.Find(prefabId);
        if (owner != null && owner.Document is not PrefabDocument) return false;
        var prefab = owner?.Document as PrefabDocument;
        if (prefab?.HasUnloadableObjects == true) return false;
        var asset = prefab?.Asset ?? stored;
        if (asset.Hierarchy.RootParts.Count == 0) { reason = "This prefab is empty and can't be added."; return false; }
        if (PrefabDependsOn(asset, target.Asset.Id)) { reason = "This prefab depends on this asset and can't be added."; return false; }
        reason = "Add the prefab";
        return true;
    }

    private bool PrefabDependsOn(PrefabAsset prefab, AssetId target)
    {
        var pending = new Stack<EntityHierarchyAssetBase>(); pending.Push(prefab);
        var visited = new HashSet<AssetId>();
        while (pending.TryPop(out var asset))
        {
            if (asset.Id == target) return true;
            if (!visited.Add(asset.Id)) continue;
            foreach (var reference in asset.Hierarchy.Parts.Values.Select(part => part.Base?.BasePartAsset).OfType<AssetReference>())
            {
                if (reference.Id == target) return true;
                var authored = authoring?.Find(reference.Id)?.Document as EntityHierarchyDocument;
                var inherited = authored?.Asset ?? workspace?.Session.FindAsset(reference.Id)?.Asset as EntityHierarchyAssetBase;
                if (inherited != null) pending.Push(inherited);
            }
        }
        return false;
    }

    private PrefabInstanceCreation InstantiatePrefab(AssetId prefabId, EntityHierarchyDocument target, Guid? parentId = null,
        string folder = "", int index = -1, bool createContainer = true, Vector3? worldPosition = null)
    {
        if (!CanInstantiatePrefab(prefabId, target, parentId, folder, index, out var reason)) throw new InvalidOperationException(reason);
        ValidatePendingPropertyStrings();
        FinishEntityRename(true); documents.CommitAll();
        var item = workspace!.FindAsset(prefabId);
        var path = item.FullPath.ToOSPath();
        var source = GetCanonicalDocument(path, () => new PrefabDocument(path, sessionHistory, item.Package.IsReadOnly)) as PrefabDocument
            ?? throw new InvalidOperationException("The prefab has a different canonical authoring document.");
        var scenePosition = worldPosition is { } position ? position - GetSceneHierarchyOffset(target) : (Vector3?)null;
        var result = target.InsertPrefab(source, item.Location.ToString(), parentId, folder, index, createContainer, scenePosition);
        assetDeletionCommandContext = assetRelocationCommandContext = packageFolderCommandContext = assetTagBatchCommandContext = assetCommandContext = false;
        selectedHierarchyFolder = null; revealCreatedParent = parentId;
        engineViewport?.Picker?.Cancel("Prefab insertion");
        return result;
    }
    private Vector3 GetSceneHierarchyOffset(EntityHierarchyDocument target)
    {
        var result = Vector3.Zero;
        var visited = new HashSet<AssetId>();
        var scene = target.Asset as SceneAsset;
        while (scene != null)
        {
            if (!visited.Add(scene.Id)) throw new InvalidOperationException("The scene hierarchy contains a cycle.");
            result += scene.Offset;
            if (scene.Parent == null) break;
            var reference = AttachedReferenceManager.GetAttachedReference(scene.Parent)
                ?? throw new InvalidOperationException("The parent scene reference cannot be resolved.");
            scene = (authoring?.Find(reference.Id)?.Document as EntityHierarchyDocument)?.Asset as SceneAsset
                ?? workspace?.Session.FindAsset(reference.Id)?.Asset as SceneAsset
                ?? throw new InvalidOperationException("The parent scene is not available.");
        }
        return result;
    }
}
