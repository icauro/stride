using Stride.Core.Mathematics;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void HandleSceneTransformBindings(CameraKeyBindings bindings)
    {
        if (engineViewport?.Gizmos is not { Captured: false } gizmos) return;
        RestoreSceneTransformationViewState();
        if (CameraBindingPressed(bindings.TranslationGizmo)) gizmos.Mode = 0;
        if (CameraBindingPressed(bindings.RotationGizmo)) gizmos.Mode = 1;
        if (CameraBindingPressed(bindings.ScaleGizmo)) gizmos.Mode = 2;
        if (CameraBindingPressed(bindings.SwitchGizmo)) gizmos.Mode = (gizmos.Mode + 1) % 3;
        SaveSceneTransformationViewState();
        if (CameraBindingPressed(bindings.SnapSelectionToGrid)) Execute(SnapSceneSelectionToGrid);
    }

    private void SnapSceneSelectionToGrid()
    {
        if (sessionHistory == null || engineViewport?.Gizmos is not { } gizmos
            || IsAuthoringPipelineUpdating || !float.IsFinite(gizmos.TranslationSnap) || gizmos.TranslationSnap <= 0) return;
        var owners = SelectedHierarchyDocuments.ToArray();
        var sceneRoots = SelectedSceneOffsetTransformRoots();
        if (owners.Length == 0 && sceneRoots.Length == 0) return;
        ValidatePendingPropertyStrings();
        documents.CommitAll();
        var batches = new List<(EntityHierarchyDocument Owner, ComponentField[] Fields, object?[] Values)>();
        foreach (var owner in owners)
        {
            // Native snapping consumes the game selection's normalized selectable
            // roots, including an unlocked child beneath a selected locked parent.
            var roots = SceneGizmoEntityRoots(owner);
            if (roots.Length == 0) continue;
            if (!ReferenceEquals(owner.SessionHistory, sessionHistory) || !ReferenceEquals(sessionHistory.Registry.Find(owner.Asset.Id)?.Document, owner)
                || gizmos.CanEditDocument?.Invoke(owner) != true) throw new InvalidOperationException("The selected hierarchy cannot be edited.");
            var fields = roots.Select(entity => owner.GetComponentFields(entity.Transform).First(member => member.Node.Name == nameof(TransformComponent.Position))).ToArray();
            if (fields.Any(field => !owner.CanEditAggregateProperty(field) || !owner.CanEditHierarchyField(field))) throw new InvalidOperationException("The selected position cannot be edited.");
            var values = roots.Select(entity => (object?)MathUtil.Snap(entity.Transform.Position, gizmos.TranslationSnap)).ToArray();
            if (fields.Length != 0) batches.Add((owner, fields, values));
        }
        var offsets = sceneRoots.Select(owner => (Owner: owner, Node: owner.SceneOffsetNode, Value: MathUtil.Snap(owner.Asset.Offset, gizmos.TranslationSnap))).ToArray();
        if (offsets.Any(offset => !offset.Owner.CanEditSceneOffset(offset.Node))) throw new InvalidOperationException("The selected scene offset cannot be edited.");
        if (batches.Count == 0 && offsets.Length == 0) return;
        var leases = new List<IDisposable>();
        try
        {
            foreach (var batch in batches) leases.Add(sessionHistory.Registry.Find(batch.Owner.Asset.Id)!.AcquireView());
            foreach (var offset in offsets) leases.Add(sessionHistory.Registry.Find(offset.Owner.Asset.Id)!.AcquireView());
            using var captured = sessionHistory.CaptureOperationsAtomic(() =>
            {
                foreach (var batch in batches) batch.Owner.EditAggregatePropertyFields(batch.Fields, batch.Values);
                foreach (var offset in offsets) offset.Owner.SetSceneOffset(offset.Node, offset.Value);
            });
            captured.Publish(sessionHistory, "Snap selection to the grid");
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }
}
