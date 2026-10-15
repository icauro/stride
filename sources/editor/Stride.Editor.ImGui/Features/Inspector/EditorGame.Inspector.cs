using Stride.Engine;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private PropertyGridPanel? propertyGridPanel;
    private PropertyGridPanel PropertyGrid => propertyGridPanel ??= new(new PropertyGridHost(this));
    private Entity? inspectedEntity { get => PropertyGrid.SelectedEntity; set => PropertyGrid.SelectedEntity = value; }
    private bool inspectorRenameActive { get => PropertyGrid.RenameActive; set => PropertyGrid.RenameActive = value; }
    private void DrawInspectorPanel()
    {
        using var referenceOwners = BeginContentReferenceOwnerScope(SelectedHierarchyDocuments.Cast<IEditorDocument>().ToArray());
        PropertyGrid.Draw(ref showInspector);
    }
    private static float InspectorPropertyLabelWidth() => PropertyGridRows.LabelWidth();
    private static void DrawInspectorAxisBadge(int axis) => PropertyGridRows.DrawAxisBadge(axis);
    private bool BeginInspectorPropertyRow(string label, bool drawLabel = true) => PropertyGridRows.BeginRow(label, drawLabel, DrawNativeLabel);

    private void ActivateAssetPropertyContext()
    {
        PropertyGrid.AssetSelectionActive = true;
        PropertyGrid.AssetContextScene = document;
        PropertyGrid.AssetContextSceneSelection = document?.SelectionVersion ?? 0;
    }

    private void ClearAssetPropertyContext() => PropertyGrid.AssetSelectionActive = false;

    private bool IsCurrentAssetPropertyContext => PropertyGrid.AssetSelectionActive && workspace != null && assetBrowser.SelectedCount > 0
        && ReferenceEquals(PropertyGrid.AssetContextScene, document) && PropertyGrid.AssetContextSceneSelection == (document?.SelectionVersion ?? 0);

    private bool IsPropertyGridAssetTagContext(AssetMetadataDocument metadata) => PropertyGrid.Visible && IsCurrentAssetPropertyContext
        && assetBrowser.SelectedCount == 1 && assetBrowser.PrimaryEntry is { } asset
        && string.Equals(asset.Path, metadata.Path, StringComparison.OrdinalIgnoreCase);

    private IEditorDocument? PropertyGridCommandDocument => IsCurrentAssetPropertyContext && assetBrowser.SelectedCount == 1
        && assetBrowser.PrimaryEntry is { } asset ? authoring?.Find(asset.Path)?.Document : null;

    private bool TryDrawSelectedAssetProperties()
    {
        if (TryDrawUIElementProperties()) return true;
        if (TryDrawSelectedCompositorProperties()) return true;
        if (TryDrawSelectedSpriteProperties()) return true;
        if (!IsCurrentAssetPropertyContext) { ClearAssetPropertyContext(); return false; }
        if (assetBrowser.SelectedCount == 1 && assetBrowser.PrimaryEntry is { } asset)
        {
            if (typeof(Stride.Assets.Entities.SceneAsset).IsAssignableFrom(asset.AssetType))
            {
                SceneDocument? scene = authoring?.Find(asset.Path)?.Document as SceneDocument;
                if (scene == null) Execute(() => scene = GetAssetTagDocument(asset, activate: false, openView: false) as SceneDocument);
                if (scene != null) { PropertyGrid.DrawSceneRootProperties(scene); DrawSelectedAssetTags(); return true; }
            }
            var metadata = authoring?.Find(asset.Path)?.Document as AssetMetadataDocument;
            if (metadata == null && !typeof(Stride.Core.Assets.SourceCodeAsset).IsAssignableFrom(asset.AssetType))
                Execute(() => metadata = GetAssetTagDocument(asset, activate: false, openView: false) as AssetMetadataDocument);
            if (metadata != null) { DrawAssetMetadataDocument(metadata, inPropertyGrid: true); return true; }
        }
        DrawSelectedAssetTags();
        return true;
    }

    private sealed class PropertyGridHost(EditorGame editor) : IPropertyGridHost
    {
        public EntityHierarchyDocument? Document => editor.document;
        public SceneDocument? SelectedSceneRoot
        {
            get
            {
                var content = editor.SelectionService.HierarchyContent;
                var roots = editor.SelectionService.SelectedSceneRoots;
                if (content.Count != 0 || roots.Count != 1) return null;
                var scene = roots[0];
                return editor.IsCurrentPresentedSceneRoot(scene)
                    && ReferenceEquals(editor.authoring?.Find(scene.Asset.Id)?.Document, scene) ? scene : null;
            }
        }
        public bool IsCurrentScenePropertyOwner(SceneDocument owner)
            => ReferenceEquals(SelectedSceneRoot, owner) || editor.IsCurrentAssetPropertyContext
                && editor.assetBrowser.SelectedCount == 1 && editor.assetBrowser.PrimaryEntry is { } asset
                && string.Equals(asset.Path, owner.Path, StringComparison.OrdinalIgnoreCase)
                && ReferenceEquals(editor.authoring?.Find(owner.Asset.Id)?.Document, owner);
        public void ReportScenePropertyFailure(Exception error) => editor.statusMessage = error.Message;
        public int CompositeSelectionCount => editor.SelectedHierarchyDocuments.Count;
        public bool ReadOnly => editor.document?.CanSave == false || editor.document is SceneDocument scene && (editor.IsSceneLocked(scene) || !editor.IsSceneLoaded(scene));
        public UiControls Controls => editor.Controls;
        public Entity? RenameEntity => editor.renameEntity;
        public string RenameDraft { get => editor.renameDraft; set => editor.renameDraft = value; }
        public void MigrateWindowTitle(string previous, string current) => editor.MigrateLiveWindowTitle(previous, current);
        public void RecordDocked() => editor.dockedPanels++;
        public bool TryDrawPackageProperties() => editor.TryDrawPackageInspector();
        public bool TryDrawSelectedAssetProperties() => editor.TryDrawSelectedAssetProperties();
        public void ActivateSceneContext() => editor.UseSceneDocumentContext();
        public void DrawAggregateProperties() => editor.DrawAggregateInspector();
        public void DrawCommonProperties() => editor.DrawCommonInspector();
        public void DrawInheritedEntityResetMenu(Entity entity) => editor.DrawInheritedEntityResetMenu(entity);
        public void DrawAddComponent(Entity entity) => editor.DrawAddComponent(entity);
        public void DrawTransformActions(TransformComponent transform) => editor.DrawRemoveComponentAction(transform);
        public void DrawInheritedTransform(TransformComponent transform) => editor.DrawInheritedTransform(transform);
        public void DrawComponents(Entity entity) => editor.DrawComponentInspector(entity);
        public void DrawAssetComponentDrop() => editor.DrawInspectorAssetComponentDrop();
        public void ReportPropertyFailure(ComponentField field, Exception error)
        { editor.componentErrors[field] = error.Message; editor.statusMessage = error.Message; }
        public IDisposable AcquirePropertyDraftOwner(EntityHierarchyDocument owner) => editor.AcquirePropertyStringOwners([owner]);
        public void BeginRename(Entity entity) { editor.Execute(() => editor.BeginEntityRename(entity)); editor.focusRename = false; }
        public void FinishRename(bool commit) { if (commit) editor.Execute(() => editor.FinishEntityRename(true)); else editor.FinishEntityRename(false); }
    }
}
