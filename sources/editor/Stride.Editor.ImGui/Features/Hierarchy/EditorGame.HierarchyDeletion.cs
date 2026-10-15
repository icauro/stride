using Hexa.NET.ImGui;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private Entity[] deleteEntities = [];
    private EntityHierarchyDocument? deleteDocument;
    private sealed record OwnerDeletion(EntityHierarchyDocument Document, Entity[] Roots, long Structure);
    private OwnerDeletion[] deleteOwners = [];
    private sealed record FolderDeletion(EntityHierarchyDocument Document, Guid? Owner, string Path, long Version);
    private FolderDeletion[] deleteFolders = [];
    private int deleteCount;
    private long deleteStructureVersion;
    private bool openDeletePrompt;
    private bool deleteWithoutPrompt;
    private bool alwaysDeleteEntitiesWithoutAsking;
    internal int EngineModelCount => InspectionViewport?.ModelCount ?? 0;
    internal bool EntityReferencesMatch => document != null && InspectionViewport?.EntityReferencesMatch(document) == true;
    internal void CaptureDeleteCheckpoint() => captureCheckpointSuffix = ".hierarchy-delete.png";
    internal void CaptureDeleteUndoCheckpoint() => captureCheckpointSuffix = ".hierarchy-delete-undo.png";
    internal void CaptureDeletedModelCheckpoint() => captureCheckpointSuffix = ".hierarchy-deleted-model.png";
    private void RequestEntityDeletion(Entity entity)
        => RequestEntityDeletion(document, entity);
    private void RequestEntityDeletion(EntityHierarchyDocument? owner, Entity entity)
    {
        if (owner == null || !IsCurrentHierarchyContextOwner(owner) || !owner.CanDeleteEntity(entity)) return;
        if (owner.IsSelected(entity.Id)) RequestSelectedEntitiesDeletion();
        else RequestOwnersDeletion([new(owner, [entity], owner.StructureVersion)]);
    }

    private void RequestSelectedEntitiesDeletion()
    {
        if (!CanDeleteHierarchySelection) return;
        if (HasOnlySceneRootSelection)
        {
            RequestSelectedChildSceneRemoval();
            return;
        }
        var folders = SelectionService.HierarchyContent.Where(item => item.FolderPath != null)
            .Select(item => new FolderDeletion(item.Document, item.FolderOwner, item.FolderPath!, item.Document.FolderVersion)).ToArray();
        var owners = SelectedHierarchyDocuments.Concat(folders.Select(folder => folder.Document)).Distinct()
            .Select(owner => new OwnerDeletion(owner, owner.GetSelectedTransformRoots(), owner.StructureVersion)).ToArray();
        RequestOwnersDeletion(owners, folders);
    }

    private void RequestEntitiesDeletion(Entity[] entities)
    {
        if (document == null || entities.Length == 0 || entities.Any(entity => !document.CanDeleteEntity(entity))) return;
        RequestOwnersDeletion([new(document, entities, document.StructureVersion)]);
    }
    private void RequestOwnersDeletion(OwnerDeletion[] owners, FolderDeletion[]? folders = null)
    {
        folders ??= [];
        if (document == null || owners.Length == 0 || owners.Any(owner => owner.Roots.Any(entity => !owner.Document.CanDeleteEntity(entity)))
            || folders.Any(folder => !folder.Document.CanEditHierarchyFolders(folder.Owner))) return;
        ValidatePendingPropertyStrings();
        FinishEntityRename(true); document.EndEdits(); assetDocument?.EndEdit(); assetCommandContext = false;
        foreach (var owner in owners) owner.Document.EndEdits();
        owners = owners.Select(owner => owner with { Structure = owner.Document.StructureVersion }).ToArray();
        engineViewport?.Picker?.Cancel("Delete entity prompt");
        deleteOwners = owners; deleteEntities = owners[0].Roots; deleteDocument = document; deleteCount = owners.Sum(owner => owner.Roots.Length);
        deleteFolders = folders;
        deleteStructureVersion = document.StructureVersion; alwaysDeleteEntitiesWithoutAsking = false;
        deleteWithoutPrompt = preferences is { IsBlocked: false } && !preferences.Current.AskBeforeDeletingEntities;
        openDeletePrompt = !deleteWithoutPrompt;
    }
    private void DrawEntityDeletionDialog()
    {
        // Confirmed preference bypasses the modal, but graph mutation still waits
        // until all hierarchy rows finish submitting their frame.
        if (deleteWithoutPrompt)
        {
            deleteWithoutPrompt = false;
            if (CanApplyRequestedEntityDeletion()) Execute(ApplyRequestedEntityDeletion);
            deleteEntities = []; deleteOwners = []; deleteFolders = []; deleteDocument = null;
        }
        var confirmationTitle = WpfMessageDialogTitle("DeleteEntityConfirmation");
        if (openDeletePrompt) { Gui.OpenPopup(confirmationTitle); openDeletePrompt = false; }
        Gui.SetNextWindowSize(new System.Numerics.Vector2(480, 0), ImGuiCond.Appearing);
        if (!Gui.BeginPopupModal(confirmationTitle, ImGuiWindowFlags.AlwaysAutoResize)) return;
        DrawWrappedText(deleteCount <= 1 ? "Are you sure you want to delete this entity?" : $"Are you sure you want to delete these {deleteCount} entities?");
        Gui.BeginDisabled(preferences == null || preferences.IsBlocked);
        Gui.Checkbox("Always delete entities without asking", ref alwaysDeleteEntitiesWithoutAsking);
        Gui.EndDisabled();
        Gui.Separator();
        Gui.BeginDisabled(!CanApplyRequestedEntityDeletion());
        Gui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.60f, 0.24f, 0.27f, 1));
        Gui.PushStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(0.74f, 0.29f, 0.32f, 1));
        Gui.PushStyleColor(ImGuiCol.ButtonActive, new System.Numerics.Vector4(0.47f, 0.18f, 0.21f, 1));
        if (Gui.Button(EditorLocalization.Widget("Delete", "Button"), new System.Numerics.Vector2(100, 0)))
        {
            var deleted = false;
            Execute(() =>
            {
                if (alwaysDeleteEntitiesWithoutAsking) PersistEntityDeletionConfirmation(false);
                ApplyRequestedEntityDeletion(); deleted = true;
            });
            if (deleted) { deleteEntities = []; deleteOwners = []; deleteFolders = []; deleteDocument = null; Gui.CloseCurrentPopup(); }
        }
        if (Controls.Enabled) Controls.Record("hierarchy/delete/confirm"); Gui.PopStyleColor(3); Gui.EndDisabled(); Gui.SameLine();
        if (Gui.Button(EditorLocalization.Widget("Cancel", "Button"), new System.Numerics.Vector2(100, 0)) || Gui.IsKeyPressed(ImGuiKey.Escape))
        { deleteEntities = []; deleteOwners = []; deleteFolders = []; deleteDocument = null; Gui.CloseCurrentPopup(); }
        if (Controls.Enabled) Controls.Record("hierarchy/delete/cancel"); Gui.SetItemDefaultFocus();
        Gui.EndPopup();
    }

    private bool CanApplyRequestedEntityDeletion() => ReferenceEquals(document, deleteDocument) && document != null && deleteOwners.Length > 0
        && deleteOwners.All(owner => owner.Document.StructureVersion == owner.Structure && owner.Roots.All(owner.Document.CanDeleteEntity)
            && IsCurrentHierarchyContextOwner(owner.Document))
        && deleteFolders.All(folder => folder.Document.FolderVersion == folder.Version && folder.Document.CanEditHierarchyFolders(folder.Owner)
            && folder.Document.GetHierarchyFolders(folder.Owner).Contains(folder.Path, StringComparer.OrdinalIgnoreCase));
    private void ApplyRequestedEntityDeletion()
    {
        if (!CanApplyRequestedEntityDeletion()) throw new InvalidOperationException("The selected scene hierarchy changed.");
        ValidatePendingPropertyStrings();
        void Apply()
        {
            foreach (var owner in deleteOwners) if (owner.Roots.Length > 0) owner.Document.DeleteEntities(owner.Roots);
            foreach (var group in deleteFolders.GroupBy(folder => folder.Document))
                group.Key.RemoveHierarchyFolders(group.Select(folder => (folder.Owner, folder.Path)));
            SelectionService.ClearHierarchyContent(); selectedHierarchyFolder = null;
        }
        if (sessionHistory == null && deleteOwners.Length == 1) { Apply(); return; }
        using var transaction = sessionHistory!.CreateTransaction("Delete entities");
        if (sessionHistory.IsCapturingOperations) Apply();
        else
        {
            using var capture = sessionHistory.CaptureOperationsAtomic(Apply);
            capture.Publish(sessionHistory, "Delete entities");
        }
    }

    private void PersistEntityDeletionConfirmation(bool ask)
    {
        if (preferences == null || preferences.IsBlocked) throw new InvalidOperationException(preferences?.Error ?? "Editor preferences are unavailable.");
        var previous = preferences.Current.AskBeforeDeletingEntities;
        preferences.Current.AskBeforeDeletingEntities = ask;
        if (preferences.Save()) return;
        preferences.Current.AskBeforeDeletingEntities = previous;
        throw new IOException(preferences.Error ?? "The editor preferences could not be saved.");
    }
}
