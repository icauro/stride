using Stride.Engine;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal interface IHierarchyPanelHost
{
    EntityHierarchyDocument? Document { get; }
    EntityHierarchyDocument? PresentedDocument { get; }
    Entity? RenameEntity { get; }
    (EntityHierarchyDocument Document, Entity Entity)? PrimarySelection { get; }
    (Guid? Owner, string Path)? SelectedFolder { get; }
    bool PrimaryHierarchyRootSelected { get; }
    void RegisterPrefabRootRow(PrefabDocument owner);
    bool IsPrefabRootSelected(PrefabDocument owner);
    EditorIcons Icons { get; }
    EditorTheme Theme { get; }
    UiControls Controls { get; }
    void RecordDocked();
    void ActivateSceneContext();
    bool HierarchyIconButton(string name, string tooltip);
    Vector2 MeasureTreeText(string text, uint weight = 400);
    void DrawTreeText(string text, Vector2 origin, Vector2 minimum, Vector2 maximum, uint? color = null, uint weight = 400);
    void Execute(Action action);
    void CreateEmptyEntity(Entity? parent);
    bool CanCreateEntityInSelection { get; }
    void CreateEmptyEntityInSelection();
    void DrawEntityFactories();
    void DrawHierarchyFolderCreationMenu();
    void RegisterVisualCaptureRegion(string id, string kind, string name, Vector2 min, Vector2 max, string? parentId);
    void BeginEntityRename(Entity entity);
    void BeginEntityRename(EntityHierarchyDocument owner, Entity entity);
    void RequestHierarchyFolderRename(Guid? owner, string path);
    void RequestSelectedEntitiesDeletion();
    void FocusEntity(EntityHierarchyDocument owner, Entity entity);
    void HandleHierarchyClipboardShortcuts();
    void DrawCompositeSceneHierarchy(SceneDocument root);
    void DrawEntityDragDrop(Entity? entity);
    void DrawHierarchyEmptyAreaDrop();
    void DrawHierarchyChildren(Entity? parent);
    void DrawHierarchyRootContext(EntityHierarchyDocument owner);
    void ApplyHierarchyDrop();
    void ApplyHierarchyFolderDrop();
    void DrawHierarchyFolderRename();
    void ApplyHierarchyPrefabNavigation();
    bool IsPresentedHierarchy(EntityHierarchyDocument owner);
    void ActivateSceneRoot(SceneDocument scene);
    void PrepareHierarchySelection();
    void ClearOtherHierarchySelection(EntityHierarchyDocument owner);
    void SetPrimarySelection(EntityHierarchyDocument owner, Guid entityId);
    void BeginHierarchyContentRows();
    void CompleteHierarchyContentSelection();
    void QueueHierarchyEntityClick(EntityHierarchyDocument owner, Guid id, bool toggle, bool range);
    void QueueHierarchyKeyboardSelection(object target, bool toggle, bool range);
    bool DrawMenuItem(string label, string? shortcut = null, bool selected = false, bool enabled = true);
}
