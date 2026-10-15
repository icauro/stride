using Hexa.NET.ImGui;
using Stride.Assets.Presentation.AssetEditors.GameEditor;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static readonly string[] transformModes = ["Translation", "Rotation", "Scale"];
    private static readonly string[] transformSpaces = ["Use world coordinates for transformations", "Use local coordinates for transformations", "Use current camera projection coordinates for transformations"];
    private bool CanDuplicateSceneGizmoSelection
    {
        get
        {
            if (engineViewport == null || document == null || sessionHistory == null) return false;
            var any = false;
            foreach (var owner in SelectedHierarchyDocuments)
            {
                var roots = engineViewport.GetWorldTransformRoots(owner);
                if (roots.Length == 0) continue;
                if (!owner.CanDuplicateEntitySubset(roots) || !owner.CanTransformSubset(roots)) return false;
                any = true;
            }
            return any;
        }
    }
    private void DuplicateSceneGizmoSelection()
    {
        if (!CanDuplicateSceneGizmoSelection || document == null || engineViewport == null)
            throw new InvalidOperationException("The current scene selection cannot be duplicated.");
        ValidatePendingPropertyStrings();
        documents.CommitAll();
        var plans = SelectedHierarchyDocuments.Select(owner => (Owner: owner, Roots: engineViewport.GetWorldTransformRoots(owner)))
            .Where(plan => plan.Roots.Length > 0).Select(plan => (plan.Owner, plan.Roots,
                Preserved: plan.Owner.SelectedIds.Where(id => !engineViewport.IsExplicitWorldSelection(plan.Owner, id)).ToArray())).ToArray();
        if (plans.Length == 0 || plans.Any(plan => !plan.Owner.CanDuplicateEntitySubset(plan.Roots) || !plan.Owner.CanTransformSubset(plan.Roots)))
            throw new InvalidOperationException("The current scene selection cannot be duplicated.");
        FinishEntityRename(true); engineViewport.Picker?.Cancel("Duplicate entities");
        ApplyCompositeHierarchyCommand(plans.Select(plan => plan.Owner).ToArray(), "Duplicate selection", owner =>
        {
            var plan = plans.First(plan => ReferenceEquals(plan.Owner, owner));
            owner.DuplicateEntitySubset(plan.Roots, plan.Preserved);
        });
        SelectionService.ClearHierarchyContent(); selectedHierarchyFolder = null;
        var primaryOwner = CompositeHierarchyPrimary?.Document;
        var primaryPlan = plans.FirstOrDefault(plan => ReferenceEquals(plan.Owner, primaryOwner));
        var nextPrimaryOwner = primaryPlan.Owner ?? plans[^1].Owner;
        if (nextPrimaryOwner.Selected is { } primary) compositeHierarchyPrimary = (nextPrimaryOwner, primary.Id);
        revealCreatedParent = nextPrimaryOwner.Selected?.Transform.Parent?.Entity.Id;
        scrollHierarchyEntity = nextPrimaryOwner.Selected?.Id; hierarchyFilter = "";
        // The shared native gizmo resumes against the actual selected copies,
        // whose engine instances must exist before it records starting transforms.
        engineViewport.Synchronize(document);
    }
    private void DrawTransformGizmoToolbar(SceneToolbarContext? context = null)
    {
        if (context != null && !context.Current()) return;
        var engineViewport = context?.Viewport ?? this.engineViewport;
        if (engineViewport?.Gizmos is not { } gizmos) return;
        if (context == null) RestoreSceneTransformationViewState();
        for (var index = 0; index < transformModes.Length; index++)
        {
            var icon = index == 0 ? "scene-toolbar-translate" : index == 1 ? "scene-toolbar-rotate" : "scene-toolbar-scale";
            if (SceneToolbarIcon(icon, transformModes[index], gizmos.Mode == index)) gizmos.Mode = index;
        }
        if (SceneToolbarIcon("scene-toolbar-snap", gizmos.Mode == 1 ? "Snap rotations to this value" : gizmos.Mode == 2 ? "Snap scale to this factor" : "Snap translations to this value", gizmos.Snap)) gizmos.Snap = !gizmos.Snap;
        var value = gizmos.Mode == 1 ? gizmos.RotationSnap : gizmos.Mode == 2 ? gizmos.ScaleSnap : gizmos.TranslationSnap;
        NextSceneToolbarItem(UiScale(50)); Gui.SetNextItemWidth(UiScale(50));
        if (Gui.InputFloat("##Transform snap value", ref value, 0, 0, "%.3f") && float.IsFinite(value) && value > 0)
        {
            if (gizmos.Mode == 1) gizmos.RotationSnap = value;
            else if (gizmos.Mode == 2) gizmos.ScaleSnap = value;
            else gizmos.TranslationSnap = value;
        }
        SceneToolbarSeparator();
        for (var index = 0; index < transformSpaces.Length; index++)
        {
            if (gizmos.Mode == 2 && index != (int)TransformationSpace.ObjectSpace) continue;
            var icon = index == 0 ? "scene-toolbar-world" : index == 1 ? "scene-toolbar-local" : "scene-toolbar-view";
            if (SceneToolbarIcon(icon, transformSpaces[index], (int)gizmos.Space == index)) gizmos.Space = (TransformationSpace)index;
        }
        if (context == null) SaveSceneTransformationViewState();
        SceneToolbarSeparator();
    }
}
