using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private System.Numerics.Vector2? sceneContextPress;
    private bool sceneContextDragged;
    private WeakReference<EntityHierarchyDocument>? sceneContextOwner;
    private WeakReference<EngineViewport>? sceneContextViewport;
    private WeakReference<EntityHierarchyDocument>? sceneContextPresentation;
    private System.Numerics.Vector2 sceneContextMousePosition;

    private void DrawSceneContextMenu()
    {
        var hovered = Gui.IsItemHovered(); var io = Gui.GetIO();
        if (hovered && Gui.IsMouseClicked(ImGuiMouseButton.Right) && !io.KeyAlt && !io.KeyCtrl
            && engineViewport?.CameraOrientation?.HasPointerSelection != true && engineViewport?.Gizmos?.Captured != true)
        {
            sceneContextPress = io.MousePos; sceneContextDragged = false;
            sceneContextOwner = document == null ? null : new(document);
            sceneContextViewport = engineViewport == null ? null : new(engineViewport);
            sceneContextPresentation = engineViewport?.PresentedDocument is { } presented ? new(presented) : null;
        }
        if (sceneContextPress.HasValue && (cameraPointerMoved || Gui.IsMouseDragging(ImGuiMouseButton.Right))) sceneContextDragged = true;
        if (Gui.IsMouseReleased(ImGuiMouseButton.Right))
        {
            if (sceneContextPress is { } press && hovered && !sceneContextDragged && document != null
                && ReferenceEquals(document, SceneContextOwner()) && IsSceneContextPresentationCurrent() && !io.KeyAlt && !io.KeyCtrl
                && System.Numerics.Vector2.Distance(press, io.MousePos) <= io.MouseDragThreshold)
            {
                sceneContextMousePosition = press;
                Gui.OpenPopup("Scene viewport actions");
            }
            sceneContextPress = null;
        }
        if (!Gui.IsMouseDown(ImGuiMouseButton.Right)) sceneContextPress = null;
        if (!Gui.BeginPopup("Scene viewport actions")) return;
        var owner = SceneContextOwner();
        var available = projectLoading == null && !closeRequested && !closePromptOpen && !IsAuthoringPipelineUpdating
            && owner != null && ReferenceEquals(document, owner)
            && IsCurrentHierarchyContextOwner(owner) && IsSceneContextPresentationCurrent();
        var canCreate = available && !IsAuthoringPipelineUpdating && owner!.CanCreateEntity(null);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Empty entity", "Menu"), "", false, canCreate))
            QueueSceneContextCreation(owner!, () => new Entity("Entity"));
        foreach (var category in EntityCreationFactories.Categories)
        {
            if (!BeginNativeMenu(category, canCreate)) continue;
            if (category == "Model")
            {
                DrawSceneContextModelFactories(owner!, false);
                DrawSceneContextModelFactories(owner!, true);
            }
            foreach (var factory in EntityCreationFactories.Registered.Where(factory => factory.Category == category))
                if (DrawNativeMenuItem(factory.Label))
                {
                    if (factory.Kind == EditorEntityKind.SkyboxLight)
                        pendingHierarchyPrefabNavigation = () => RequestSkyboxLightCreation(owner!, null, "", viewport: true);
                    else QueueSceneContextCreation(owner!, () => EntityCreationFactories.Create(factory.Kind, AuthoredCameraSlot()));
                }
            Gui.EndMenu();
        }
        Gui.Separator();
        if (available) DrawHierarchyPrefabNavigationMenu(owner!, action => pendingHierarchyPrefabNavigation = action, "Open in prefab editor");
        Gui.Separator();
        if (DrawNativeMenuItem(EditorLocalization.Widget("Duplicate", "Menu"), "", false, available && CanDuplicateHierarchyClipboard)) Execute(DuplicateHierarchySelection);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Cut", "Menu"), "", false, available && CanCutHierarchyClipboard)) Execute(() => CopyHierarchySelection(true));
        if (DrawNativeMenuItem(EditorLocalization.Widget("Copy", "Menu"), "", false, available && CanCopyHierarchyClipboard)) Execute(() => CopyHierarchySelection(false));
        if (DrawNativeMenuItem(EditorLocalization.Widget("Delete", "Menu"), "", false, available && CanDeleteHierarchySelection)) RequestSelectedEntitiesDeletion();
        Gui.Separator();
        var primary = CompositeHierarchyPrimary;
        if (DrawNativeMenuItem(EditorLocalization.Widget("Rename", "Menu"), "", false, available && primary is { } rename && rename.Document.CanRenameEntity(rename.Entity)))
            BeginEntityRename(primary!.Value.Document, primary.Value.Entity);
        Gui.EndPopup();
    }
    private EntityHierarchyDocument? SceneContextOwner()
        => sceneContextOwner != null && sceneContextOwner.TryGetTarget(out var owner) ? owner : null;
    private bool IsSceneContextPresentationCurrent() => IsSceneContextPresentationCurrent(sceneContextViewport, sceneContextPresentation);
    private bool IsSceneContextPresentationCurrent(WeakReference<EngineViewport>? viewportSource,
        WeakReference<EntityHierarchyDocument>? presentationSource) => projectContent != null && sceneViewportVisible
        && SceneImageSize.X > 0 && SceneImageSize.Y > 0
        && viewportSource?.TryGetTarget(out var viewport) == true && ReferenceEquals(viewport, engineViewport)
        && presentationSource?.TryGetTarget(out var presented) == true && ReferenceEquals(viewport.PresentedDocument, presented);
    private void DrawSceneContextModelFactories(EntityHierarchyDocument owner, bool instanced)
    {
        if (DrawNativeMenuItem(instanced ? "Instanced Model" : "Model", "", false, owner.CanCreateEntity(null) && !IsAuthoringPipelineUpdating))
            pendingHierarchyPrefabNavigation = () => RequestModelEntityCreation(owner, null, "", instanced, viewport: true);
    }
    private void QueueSceneContextCreation(EntityHierarchyDocument owner, Func<Entity> create)
    {
        var mousePosition = sceneContextMousePosition;
        var viewportSource = sceneContextViewport;
        var presentationSource = sceneContextPresentation;
        pendingHierarchyPrefabNavigation = () =>
        {
            if (projectLoading != null || closeRequested || closePromptOpen || IsAuthoringPipelineUpdating || !ReferenceEquals(document, owner)
                || !IsCurrentHierarchyContextOwner(owner) || !owner.CanCreateEntity(null)
                || !IsSceneContextPresentationCurrent(viewportSource, presentationSource))
                throw new InvalidOperationException("The scene creation destination changed.");
            ValidatePendingPropertyStrings();
            if (!ReferenceEquals(document, owner) || !IsCurrentHierarchyContextOwner(owner) || !owner.CanCreateEntity(null)
                || !IsSceneContextPresentationCurrent(viewportSource, presentationSource)) throw new InvalidOperationException("The scene creation destination changed.");
            var normalized = (mousePosition - SceneImageMinimum) / SceneImageSize;
            var position = ScenePointAt(new Vector2(normalized.X, normalized.Y));
            var entity = create();
            // Native CreateEntity(atMousePosition) writes this point directly. Asset drop uses TransformToLocalRoot separately.
            entity.Transform.Position = position;
            InsertContextEntity(owner, null, "", entity);
        };
    }
}
