using Hexa.NET.ImGui;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private Entity? movingEntity;
    private WeakReference<EntityHierarchyDocument>? movingHierarchyOwner;
    private bool keepWorldOnMove;
    private bool openMovePopup;
    internal void CaptureMoveCheckpoint() => captureCheckpointSuffix = ".hierarchy-move.png";
    internal void CaptureMovedCheckpoint() => captureCheckpointSuffix = ".hierarchy-moved.png";
    internal bool SelectedWorldMatches => document?.Selected != null && InspectionViewport?.MatchesWorldTransform(document.Selected) == true;
    private void RequestEntityMove(Entity entity)
        => RequestEntityMove(document, entity);
    private void RequestEntityMove(EntityHierarchyDocument? owner, Entity entity)
    {
        if (owner == null || IsAuthoringPipelineUpdating || !IsCurrentHierarchyContextOwner(owner) || !owner.CanDeleteEntity(entity)) return;
        ValidatePendingPropertyStrings();
        FinishEntityRename(true); owner.EndEdits(); assetDocument?.EndEdit(); assetCommandContext = false;
        if (!IsCurrentHierarchyContextOwner(owner) || !owner.CanDeleteEntity(entity)) return;
        movingHierarchyOwner = new(owner);
        movingEntity = entity; keepWorldOnMove = Gui.GetIO().KeyAlt; openMovePopup = true;
    }
    private void DrawEntityMovePopup()
    {
        if (openMovePopup) { Gui.OpenPopup("Move entity"); openMovePopup = false; }
        if (!Gui.BeginPopup("Move entity")) return;
        var owner = movingHierarchyOwner?.TryGetTarget(out var targetOwner) == true ? targetOwner : null;
        if (owner == null || !IsCurrentHierarchyContextOwner(owner) || movingEntity == null
            || !owner.Asset.Hierarchy.Parts.TryGetValue(movingEntity.Id, out var part) || !ReferenceEquals(part.Entity, movingEntity))
        { Gui.CloseCurrentPopup(); Gui.EndPopup(); return; }
        Gui.TextUnformatted("Move " + movingEntity.Name);
        Gui.Checkbox("Keep world transform", ref keepWorldOnMove);
        if (Controls.Enabled) Controls.Record("hierarchy/move/keep-world");
        Gui.Separator();
        var shouldMove = false; Entity? destination = null;
        void Target(Entity? target)
        {
            Gui.PushID(target?.Id.ToString() ?? "root");
            Gui.BeginDisabled(!owner.CanReparent(movingEntity, target));
            var origin = Gui.GetCursorScreenPos();
            var label = target?.Name ?? "Scene root";
            if (Gui.Selectable("##Destination", false, ImGuiSelectableFlags.None,
                new System.Numerics.Vector2(Math.Max(190, Gui.CalcTextSize(label).X + 12), 0)))
            {
                shouldMove = true; destination = target;
                Gui.CloseCurrentPopup();
            }
            if (Controls.Enabled) Controls.Record("hierarchy/move/" + (target?.Id.ToString() ?? "root"));
            Gui.GetWindowDrawList().AddText(origin, Gui.GetColorU32(ImGuiCol.Text), label);
            Gui.EndDisabled(); Gui.PopID();
        }
        Target(null);
        foreach (var candidate in owner.Asset.Hierarchy.Parts.Values) Target(candidate.Entity);
        if (shouldMove)
        {
            Execute(() =>
            {
                if (IsAuthoringPipelineUpdating || !IsCurrentHierarchyContextOwner(owner) || !owner.CanReparent(movingEntity, destination)) return;
                ValidatePendingPropertyStrings();
                if (!IsCurrentHierarchyContextOwner(owner) || !owner.CanReparent(movingEntity, destination)) return;
                owner.Reparent(movingEntity, destination, keepWorldOnMove);
            });
            RevealHierarchyContext(owner, movingEntity.Id, destination?.Id, owner.Asset.Hierarchy.Parts[movingEntity.Id].Folder);
            engineViewport?.Picker?.Cancel("Scene parent changed");
        }
        Gui.EndPopup();
    }
}
