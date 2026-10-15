using Stride.Engine;

namespace Stride.Editor.ImGui;

internal interface IPropertyGridHost
{
    EntityHierarchyDocument? Document { get; }
    SceneDocument? SelectedSceneRoot { get; }
    bool IsCurrentScenePropertyOwner(SceneDocument owner);
    void ReportScenePropertyFailure(Exception error);
    int CompositeSelectionCount { get; }
    bool ReadOnly { get; }
    UiControls Controls { get; }
    Entity? RenameEntity { get; }
    string RenameDraft { get; set; }
    void MigrateWindowTitle(string previous, string current);
    void RecordDocked();
    bool TryDrawPackageProperties();
    bool TryDrawSelectedAssetProperties();
    void ActivateSceneContext();
    void DrawAggregateProperties();
    void DrawCommonProperties();
    void DrawInheritedEntityResetMenu(Entity entity);
    void DrawAddComponent(Entity entity);
    void DrawTransformActions(TransformComponent transform);
    void DrawInheritedTransform(TransformComponent transform);
    void DrawComponents(Entity entity);
    void DrawAssetComponentDrop();
    void BeginRename(Entity entity);
    void FinishRename(bool commit);
    void ReportPropertyFailure(ComponentField field, Exception error);
    IDisposable AcquirePropertyDraftOwner(EntityHierarchyDocument owner);
}
