using Hexa.NET.ImGui;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private Entity? renameEntity;
    private EntityHierarchyDocument? renameOwner;
    private string renameDraft = "";
    private bool focusRename;
    private bool HasPendingRenameChanges => renameEntity != null && renameDraft != renameEntity.Name || HasPendingHierarchyFolderRenameChanges;
    internal bool SelectedEngineNameMatches => document?.Selected != null && engineViewport?.MatchesName(document.Selected) == true;
    private void BeginEntityRename(Entity entity) => BeginEntityRename(document, entity);
    private void BeginEntityRename(EntityHierarchyDocument? owner, Entity entity)
    {
        FinishEntityRename(true);
        inspectorRenameActive = false;
        if (owner == null || IsAuthoringPipelineUpdating || !IsCurrentHierarchyContextOwner(owner) || !owner.CanRenameEntity(entity)) return;
        ValidatePendingPropertyStrings();
        if (!IsCurrentHierarchyContextOwner(owner) || !owner.CanRenameEntity(entity)) return;
        owner.EndEdits();
        if (!owner.IsSelected(entity.Id)) SelectCompositeHierarchyEntity(owner, entity, false);
        renameOwner = owner; renameEntity = entity; renameDraft = entity.Name; focusRename = true;
        RevealHierarchyContext(owner, entity.Id, entity.Transform.Parent?.Entity.Id, owner.Asset.Hierarchy.Parts[entity.Id].Folder);
    }
    private void FinishEntityRename(bool commit)
    {
        FinishHierarchyFolderRename(commit);
        if (renameEntity == null) return;
        var entity = renameEntity;
        if (commit && entity.Name != renameDraft)
        {
            if (!IsCurrentHierarchyContextOwner(renameOwner!) || !renameOwner!.CanRenameEntity(entity))
                throw new InvalidOperationException("The entity rename owner is no longer editable.");
            ValidatePendingPropertyStrings();
            if (!IsCurrentHierarchyContextOwner(renameOwner!) || !renameOwner!.CanRenameEntity(entity))
                throw new InvalidOperationException("The entity rename owner changed.");
            renameOwner.Rename(entity, renameDraft);
        }
        renameEntity = null; renameOwner = null; focusRename = false; inspectorRenameActive = false;
    }
    private static void DrawWrappedText(string value)
    {
        Gui.PushTextWrapPos(0); Gui.TextUnformatted(value); Gui.PopTextWrapPos();
    }
}
