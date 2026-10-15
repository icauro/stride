using Stride.Engine;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class HierarchyPanelHost(EditorGame editor) : IHierarchyPanelHost
    {
        public EntityHierarchyDocument? Document => editor.document;
        public EntityHierarchyDocument? PresentedDocument => editor.PresentedHierarchyDocument;
        public Entity? RenameEntity => editor.renameEntity;
        public (EntityHierarchyDocument Document, Entity Entity)? PrimarySelection => editor.CompositeHierarchyPrimary;
        public (Guid? Owner, string Path)? SelectedFolder => editor.SelectionService.PrimaryHierarchyContent is { FolderPath: { } path } folder
            ? (folder.FolderOwner, path) : null;
        public bool PrimaryHierarchyRootSelected => editor.SelectionService.PrimaryContentIsRoot;
        public void RegisterPrefabRootRow(PrefabDocument owner)
        {
            var target = HierarchyContentItem.Root(owner);
            editor.SelectionService.RegisterHierarchyRow(target);
            editor.HierarchyController.SetKeyboardRowTarget(target);
        }
        public bool IsPrefabRootSelected(PrefabDocument owner) => editor.SelectionService.IsContentSelected(HierarchyContentItem.Root(owner));
        public EditorIcons Icons => editor.icons!;
        public EditorTheme Theme => editor.preferences?.Current.Theme ?? EditorTheme.ExpressionDark;
        public UiControls Controls => editor.Controls;
        public void RecordDocked() => editor.dockedPanels++;
        public void ActivateSceneContext() => editor.UseSceneDocumentContext();
        public bool HierarchyIconButton(string name, string tooltip) => editor.HierarchyIconButton(name, tooltip);
        public Vector2 MeasureTreeText(string text, uint weight = 400) => editor.MeasureHierarchyTreeText(text, weight);
        public void DrawTreeText(string text, Vector2 origin, Vector2 minimum, Vector2 maximum, uint? color = null, uint weight = 400)
            => editor.DrawHierarchyTreeText(text, origin, minimum, maximum, color, weight);
        public void Execute(Action action) => editor.Execute(action);
        public void CreateEmptyEntity(Entity? parent) => editor.CreateEmptyEntity(parent);
        public bool CanCreateEntityInSelection => editor.CanCreateInHierarchySelection();
        public void CreateEmptyEntityInSelection() => editor.CreateHierarchyEntityInSelection(() => new Entity("Entity"));
        public void DrawEntityFactories() => editor.DrawEntityFactories();
        public void DrawHierarchyFolderCreationMenu() => editor.DrawHierarchyFolderCreationMenu();
        public void RegisterVisualCaptureRegion(string id, string kind, string name, Vector2 min, Vector2 max, string? parentId)
            => editor.RegisterVisualCaptureRegion(id, kind, name, min, max, parentId);
        public void BeginEntityRename(Entity entity) => editor.BeginEntityRename(entity);
        public void BeginEntityRename(EntityHierarchyDocument owner, Entity entity) => editor.BeginEntityRename(owner, entity);
        public void RequestHierarchyFolderRename(Guid? owner, string path) => editor.RequestHierarchyFolderRename(owner, path);
        public void RequestSelectedEntitiesDeletion() => editor.RequestSelectedEntitiesDeletion();
        public void FocusEntity(EntityHierarchyDocument owner, Entity entity) => editor.RequestHierarchyEntityFocus(owner, entity);
        public void HandleHierarchyClipboardShortcuts() => editor.HandleHierarchyClipboardShortcuts();
        public void DrawCompositeSceneHierarchy(SceneDocument root) => editor.DrawCompositeSceneHierarchy(root);
        public void DrawEntityDragDrop(Entity? entity) => editor.DrawEntityDragDrop(entity);
        public void DrawHierarchyEmptyAreaDrop() => editor.DrawHierarchyEmptyAreaDrop();
        public void DrawHierarchyChildren(Entity? parent) => editor.DrawHierarchyChildren(parent);
        public void DrawHierarchyRootContext(EntityHierarchyDocument owner) => editor.DrawHierarchyRootContext(owner);
        public void ApplyHierarchyDrop() => editor.ApplyHierarchyDrop();
        public void ApplyHierarchyFolderDrop() => editor.ApplyHierarchyFolderDrop();
        public void DrawHierarchyFolderRename() => editor.DrawHierarchyFolderRename();
        public void ApplyHierarchyPrefabNavigation() => editor.ApplyHierarchyPrefabNavigation();
        public bool IsPresentedHierarchy(EntityHierarchyDocument owner) => editor.authoring?.Find(owner.Asset.Id) is { IsPublished: true } state
            && ReferenceEquals(state.Document, owner) && (owner is SceneDocument scene ? editor.IsCurrentPresentedSceneRoot(scene)
                : ReferenceEquals(editor.PresentedHierarchyDocument, owner));
        public void ActivateSceneRoot(SceneDocument scene) => editor.ActivateSceneRoot(scene);
        public void PrepareHierarchySelection()
        {
            editor.selectedHierarchyFolder = null;
            editor.engineViewport?.Picker?.Cancel("Hierarchy selection changed");
            editor.FinishEntityRename(true); editor.assetDocument?.EndEdit(); editor.assetCommandContext = false;
        }
        public void ClearOtherHierarchySelection(EntityHierarchyDocument owner) => editor.ClearOtherHierarchySelection(owner);
        public void SetPrimarySelection(EntityHierarchyDocument owner, Guid entityId) => editor.compositeHierarchyPrimary = (owner, entityId);
        public void BeginHierarchyContentRows() => editor.SelectionService.BeginHierarchyRows();
        public void CompleteHierarchyContentSelection() => editor.CompleteHierarchyContentSelection();
        public void QueueHierarchyEntityClick(EntityHierarchyDocument owner, Guid id, bool toggle, bool range)
            => editor.QueueHierarchyEntityClick(owner, id, toggle, range);
        public void QueueHierarchyKeyboardSelection(object target, bool toggle, bool range)
        {
            if (target is SceneDocument scene) editor.SelectionService.QueueSceneRootClick(scene, toggle, range);
            else if (target is HierarchyContentItem item) editor.SelectionService.QueueHierarchyClick(item, toggle, range);
        }
        public bool DrawMenuItem(string label, string? shortcut = null, bool selected = false, bool enabled = true)
            => editor.DrawNativeMenuItem(label, shortcut, selected, enabled);
    }
}
