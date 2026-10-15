using Stride.Assets.Entities;
using Stride.Core.Assets;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private Action? pendingHierarchyPrefabNavigation;

    private AssetId[] SelectedBasePrefabs()
    {
        if (workspace == null) return [];
        var result = new HashSet<AssetId>();
        foreach (var owner in SelectedHierarchyDocuments)
        foreach (var id in owner.SelectedIds)
            if (owner.Asset.Hierarchy.Parts.TryGetValue(id, out var part) && part.Base is { } instance
                && workspace.AssetItems.Any(item => item.Id == instance.BasePartAsset.Id && item.Asset is PrefabAsset))
                result.Add(instance.BasePartAsset.Id);
        return result.ToArray();
    }
    private (EntityHierarchyDocument Owner, Guid Entity, AssetId Prefab)? PrimaryBasePrefab()
    {
        if (workspace == null || CompositeHierarchyPrimary is not { } primary
            || !IsCurrentHierarchyContextOwner(primary.Document)
            || !primary.Document.Asset.Hierarchy.Parts.TryGetValue(primary.Entity.Id, out var part)
            || !ReferenceEquals(part.Entity, primary.Entity) || part.Base is not { } instance
            || workspace.Session.FindAsset(instance.BasePartAsset.Id)?.Asset is not PrefabAsset) return null;
        return (primary.Document, primary.Entity.Id, instance.BasePartAsset.Id);
    }
    private void DrawHierarchyPrefabNavigationMenu() => DrawHierarchyPrefabNavigationMenu(document!, action => pendingHierarchyPrefabNavigation = action);
    private void DrawHierarchyPrefabNavigationMenu(EntityHierarchyDocument source, Action<Action> dispatch, string openLabel = "Open prefab in editor")
    {
        var owners = SelectedHierarchyDocuments.ToArray();
        var linkedOwners = owners.Where(owner => owner.SelectedIds.Any(id => owner.Asset.Hierarchy.Parts[id].Base != null)).ToArray();
        var prefabs = SelectedBasePrefabs();
        if (DrawNativeMenuItem(openLabel, "", false, prefabs.Length > 0))
            dispatch(() =>
            {
                if (!prefabs.ToHashSet().SetEquals(SelectedBasePrefabs()))
                    throw new InvalidOperationException("The selected prefab references changed.");
                foreach (var id in prefabs)
                {
                    var item = workspace?.AssetItems.FirstOrDefault(item => item.Id == id && item.Asset is PrefabAsset);
                    if (item != null) OpenPrefab(item.FullPath.ToOSPath());
                }
            });
        var primaryPrefab = PrimaryBasePrefab();
        if (DrawNativeMenuItem(EditorLocalization.Widget("Select prefab in asset view", "Menu"), "", false, primaryPrefab != null && owners.Sum(owner => owner.SelectedCount) == 1))
        {
            var selected = primaryPrefab!.Value;
            var selectedWorkspace = workspace;
            dispatch(() =>
            {
                if (!ReferenceEquals(workspace, selectedWorkspace) || PrimaryBasePrefab() != selected
                    || SelectedHierarchyDocuments.Sum(owner => owner.SelectedCount) != 1)
                    throw new InvalidOperationException("The selected prefab reference changed.");
                ValidatePendingPropertyStrings();
                if (!ReferenceEquals(workspace, selectedWorkspace) || PrimaryBasePrefab() != selected
                    || SelectedHierarchyDocuments.Sum(owner => owner.SelectedCount) != 1)
                    throw new InvalidOperationException("The selected prefab reference changed.");
                if (workspace != null && assetBrowser.RevealAsset(workspace, selected.Prefab) is { } entry)
                { selectedAssetPath = entry.Path; showAssets = true; }
            });
        }
        if (DrawNativeMenuItem(EditorLocalization.Widget("Break link to prefab", "Menu"), "", false, linkedOwners.Length > 0 && linkedOwners.All(owner => owner.CanBreakPrefabLinks)))
            dispatch(() => ApplyCompositeHierarchyCommand(linkedOwners, "Break link to prefab", owner => owner.BreakSelectedPrefabLinks()));
        if (DrawNativeMenuItem(EditorLocalization.Widget("Create prefab from selection", "Menu"), "", false, CanCreatePrefabFromHierarchySelection(source)))
        {
            dispatch(() => CreatePrefabFromSelection(source));
        }
    }
    private void ApplyHierarchyPrefabNavigation()
    {
        if (pendingHierarchyPrefabNavigation is not { } action) return;
        pendingHierarchyPrefabNavigation = null;
        Execute(action);
    }
}
