using Stride.Core.Assets;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly EditorSelectionHistory selectionHistory = new();
    private long observedAssetSelection = -1;
    private long observedEntitySelection = -1;
    private long observedHierarchyContent = -1;
    private Guid? observedSelectionDocument, observedSelectionEntity;
    private readonly List<(AssetId Asset, long Version)> observedHierarchySelections = [];
    private AssetId? observedPresentedHierarchy;
    private bool HierarchySelectionChanged()
    {
        if (observedHierarchyContent != SelectionService.ContentVersion) return true;
        if (observedPresentedHierarchy != PresentedHierarchyDocument?.Asset.Id) return true;
        var owners = SelectedHierarchyDocuments;
        if (owners.Count != observedHierarchySelections.Count) return true;
        for (var index = 0; index < owners.Count; index++)
            if (observedHierarchySelections[index] != (owners[index].Asset.Id, owners[index].SelectionVersion)) return true;
        return false;
    }
    private void ObserveEditorSelection()
    {
        if (workspace == null) return;
        RefreshNativeSelectionScopes();
        var entity = (CommandDocument as EntityHierarchyDocument)?.Selected?.Id;
        if (observedAssetSelection == assetBrowser.SelectionVersion && observedSelectionDocument == documents.Active?.Id
            && observedSelectionEntity == entity && observedEntitySelection == ((CommandDocument as EntityHierarchyDocument)?.SelectionVersion ?? -1)
            && !HierarchySelectionChanged() && !CompositorSelectionChanged() && !UISelectionChanged() && !SpriteSelectionChanged()) return;
        selectionHistory.Record(CaptureEditorSelection());
        RememberObservedSelection();
    }
    private EditorSelectionSnapshot CaptureEditorSelection() => new(documents.Active?.Path,
        (CommandDocument as EntityHierarchyDocument)?.SnapshotSelection().Ids ?? [],
        (CommandDocument as EntityHierarchyDocument)?.Selected?.Id,
        SelectionService.CaptureAssetSelection().OrderBy(id => id.ToString(), StringComparer.Ordinal).ToArray(), assetBrowser.Primary)
    {
        Hierarchies = SelectedHierarchyDocuments.Select(owner => new HierarchySelectionSnapshot(owner.Asset.Id, owner.Path,
            owner.SnapshotSelection().Ids, owner.Selected?.Id)).OrderBy(owner => owner.AssetId.ToString(), StringComparer.Ordinal).ToArray(),
        PresentedHierarchy = PresentedHierarchyDocument?.Asset.Id,
        PrimaryHierarchy = CompositeHierarchyPrimary?.Document.Asset.Id,
        Contents = SelectionService.CaptureHierarchyContent(),
        PrimaryContent = SelectionService.CapturePrimaryHierarchyContent(),
        SceneRoots = SelectionService.CaptureSceneRootSelection(),
        PrimarySceneRoot = SelectionService.PrimarySceneRoot,
        HierarchyContentOrder = SelectionService.CaptureHierarchySelectionOrder(),
        AssetContentFolders = assetBrowser.CaptureContentFolderSelection().OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
        AssetContentOrder = assetBrowser.CaptureContentSelectionOrder(),
        PrimaryAssetContentFolder = assetBrowser.PrimaryContentFolder,
        AssetScope = assetBrowser.CaptureNavigationScope(),
        Compositors = CaptureCompositorSelections(),
        UIElements = CaptureUISelections(),
        Sprites = CaptureSpriteSelections(),
    };
    private void RememberObservedSelection()
    {
        observedAssetSelection = assetBrowser.SelectionVersion;
        observedSelectionDocument = documents.Active?.Id;
        observedSelectionEntity = (CommandDocument as EntityHierarchyDocument)?.Selected?.Id;
        observedEntitySelection = (CommandDocument as EntityHierarchyDocument)?.SelectionVersion ?? -1;
        observedPresentedHierarchy = PresentedHierarchyDocument?.Asset.Id;
        observedHierarchyContent = SelectionService.ContentVersion;
        observedHierarchySelections.Clear();
        foreach (var owner in SelectedHierarchyDocuments) observedHierarchySelections.Add((owner.Asset.Id, owner.SelectionVersion));
        observedCompositorSelections = CaptureCompositorSelections();
        observedUISelections = CaptureUISelections();
        observedSpriteSelections = CaptureSpriteSelections();
    }
    private bool CanResolveEditorSelection(EditorSelectionSnapshot snapshot)
    {
        var entry = snapshot.DocumentPath == null ? null : documents.Find(snapshot.DocumentPath);
        var entityValid = entry?.Document is EntityHierarchyDocument scene
            && (snapshot.Entities.Length == 0 || snapshot.Entities.Any(scene.Asset.Hierarchy.Parts.ContainsKey));
        return snapshot.Sprites.Any(CanResolveSpriteSelection) || snapshot.UIElements.Any(CanResolveUISelection) || snapshot.Compositors.Any(CanResolveCompositorSelection)
            || snapshot.SceneRoots.Any(id => authoring?.Find(id)?.Document is SceneDocument)
            || snapshot.Contents.Any(item => authoring?.Find(item.AssetId)?.Document is EntityHierarchyDocument contentOwner
                && (item.IsRoot ? contentOwner is PrefabDocument : item.EntityId is { } id ? contentOwner.Asset.Hierarchy.Parts.ContainsKey(id)
                    : item.FolderPath != null && contentOwner.GetHierarchyFolders(item.FolderOwner).Contains(item.FolderPath, StringComparer.OrdinalIgnoreCase)))
            || snapshot.Hierarchies.Any(owner => authoring?.Find(owner.AssetId)?.Document is EntityHierarchyDocument retained
                && owner.Entities.Any(retained.Asset.Hierarchy.Parts.ContainsKey))
            || entry != null && (snapshot.EntityId == null || entityValid)
            || snapshot.Assets.Any(id => workspace!.Session.FindAsset(id) != null)
            || snapshot.AssetContentFolders.Any(id => assetBrowser.FindFolder(id) != null)
            || snapshot.AssetScope?.Folders.Any(id => assetBrowser.FindFolder(id) != null) == true
            || snapshot.AssetScope?.Categories.Any(id => assetBrowser.FindCategory(id) != null) == true;
    }
    private void NavigateEditorSelection(int direction)
    {
        if (IsPackageDependencyBusy) return;
        ObserveEditorSelection();
        selectionHistory.Navigate(direction, CanResolveEditorSelection, snapshot =>
        {
            ValidatePendingPropertyStrings();
            FinishEntityRename(true); CommandDocument?.CommitEdits();
            RefreshNativeSelectionScopes();
            using var restoreScopes = nativeSelectionScopes.Suppress();
            ClearPresentedHierarchySelections();
            compositeHierarchyPrimary = null;
            if (snapshot.PresentedHierarchy is { } presentedId && authoring?.Find(presentedId) is { IsPublished: true, Document: EntityHierarchyDocument presented }
                && !ReferenceEquals(PresentedHierarchyDocument, presented))
            {
                var presentedEntry = documents.Find(presented) ?? documents.Add(presented);
                ActivateDocument(presentedEntry);
            }
            var entry = snapshot.DocumentPath == null ? null : documents.Find(snapshot.DocumentPath);
            if (entry != null)
            {
                if (entry.Document is not SceneDocument child || !TryRestoreSceneHierarchyNavigation(child)) ActivateDocument(entry);
            }
            ClearPresentedHierarchySelections();
            if (snapshot.Hierarchies.Length == 0 && entry?.Document is EntityHierarchyDocument legacyOwner
                && (legacyOwner is not SceneDocument legacyScene || IsCurrentPresentedSceneRoot(legacyScene)))
                legacyOwner.RestoreSelection(snapshot.Entities, snapshot.EntityId);
            if (snapshot.Hierarchies.Length != 0)
            {
                foreach (var owner in snapshot.Hierarchies)
                    if (authoring?.Find(owner.AssetId) is { IsPublished: true, Document: EntityHierarchyDocument retained }
                        && (retained is not SceneDocument scene || IsCurrentPresentedSceneRoot(scene)))
                    {
                        retained.RestoreSelection(owner.Entities, owner.Primary);
                    }
                // The captured active editor is independent of the selected item's owner.
                // A primary owner is only a legacy fallback when that editor has closed.
                if (entry == null && snapshot.PrimaryHierarchy is { } primaryId
                    && authoring?.Find(primaryId) is { IsPublished: true, Document: SceneDocument primaryScene })
                    TryRestoreSceneHierarchyNavigation(primaryScene);
                var primaryOwner = snapshot.PrimaryHierarchy is { } ownerId ? authoring?.Find(ownerId)?.Document as EntityHierarchyDocument : null;
                compositeHierarchyPrimary = primaryOwner?.Selected is { } selected ? (primaryOwner, selected.Id) : null;
            }
            if (snapshot.Contents.Length > 0)
                SelectionService.RestoreHierarchyContent(snapshot.Contents, id => authoring?.Find(id)?.Document as EntityHierarchyDocument, snapshot.PrimaryContent);
            SelectionService.RestoreSceneRootSelection(snapshot.SceneRoots, snapshot.PrimarySceneRoot,
                id => authoring?.Find(id)?.Document as SceneDocument);
            SelectionService.RestoreHierarchySelectionOrder(snapshot.HierarchyContentOrder,
                id => authoring?.Find(id)?.Document as EntityHierarchyDocument);
            if (snapshot.AssetScope != null) assetBrowser.RestoreNavigationScope(snapshot.AssetScope);
            SelectionService.RestoreAssetSelection(snapshot.Assets, snapshot.Primary);
            assetBrowser.RestoreContentFolderSelection(snapshot.AssetContentFolders, snapshot.PrimaryAssetContentFolder);
            assetBrowser.RestoreContentSelectionOrder(snapshot.AssetContentOrder);
            RestoreCompositorSelections(snapshot.Compositors);
            RestoreUISelections(snapshot.UIElements);
            RestoreSpriteSelections(snapshot.Sprites);
            if (assetBrowser.SelectedCount == 1 && assetBrowser.PrimaryEntry is { } primary)
            {
                if (primary.Extension == ".sdtex") Execute(() => OpenTexturePreview(primary));
                else if (typeof(Stride.Assets.Sprite.SpriteSheetAsset).IsAssignableFrom(primary.AssetType)) Execute(() => OpenSpriteSheetPreview(primary));
                else if (typeof(Stride.Assets.SpriteFont.SpriteFontAsset).IsAssignableFrom(primary.AssetType)
                    || typeof(Stride.Assets.SpriteFont.PrecompiledSpriteFontAsset).IsAssignableFrom(primary.AssetType)) Execute(() => OpenFontPreview(primary));
                else if (typeof(Stride.Assets.Materials.MaterialAsset).IsAssignableFrom(primary.AssetType)) Execute(() => OpenMaterialPreview(primary));
                else if (typeof(Stride.Assets.Skyboxes.SkyboxAsset).IsAssignableFrom(primary.AssetType)) Execute(() => OpenSkyboxPreview(primary));
                else if (primary.Extension is ".sdm3d" or ".sdpromodel") Execute(() => OpenModelPreview(primary));
            }
            RememberObservedSelection();
        });
    }
    private void ClearPresentedHierarchySelections() => SelectionService.ClearPresentedHierarchies();
    private void ClearEditorSelectionHistory()
    {
        selectionHistory.Clear(); observedAssetSelection = -1;
        ClearNativeSelectionScopes();
        observedEntitySelection = -1;
        observedHierarchyContent = -1;
        observedSelectionDocument = observedSelectionEntity = null;
        observedHierarchySelections.Clear(); observedPresentedHierarchy = null;
        observedCompositorSelections = [];
        observedUISelections = [];
        observedSpriteSelections = [];
    }
    private void DrawSelectionNavigation(EditorIcons icons)
    {
        Gui.BeginDisabled(!selectionHistory.CanBack);
        if (icons.Button("selection-previous", "Previous selection")) Execute(() => NavigateEditorSelection(-1));
        Gui.EndDisabled(); Gui.SameLine();
        Gui.BeginDisabled(!selectionHistory.CanForward);
        if (icons.Button("selection-next", "Next selection")) Execute(() => NavigateEditorSelection(1));
        Gui.EndDisabled(); Gui.SameLine();
    }
}
