using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private (EntityHierarchyDocument Document, (Guid? Parent, string Folder)[] Targets)[] HierarchyPasteContentTargets()
        => SelectionService.HierarchyContent.Select(item => (item.Document,
                Parent: item.EntityId ?? item.FolderOwner, Folder: item.IsRoot || item.EntityId != null ? "" : item.FolderPath!))
            .Concat(SelectionService.SelectedSceneRoots.Select(scene => (Document: (EntityHierarchyDocument)scene, Parent: (Guid?)null, Folder: "")))
            .GroupBy(item => item.Document).Select(group =>
                (group.Key, group.Select(item => (item.Parent, item.Folder)).Distinct().ToArray())).ToArray();
    private bool CanPasteHierarchyDestinations(string text, (EntityHierarchyDocument Document, (Guid? Parent, string Folder)[] Targets)[] targets)
        => projectLoading == null && assetPreviewBuild == null && !IsAuthoringPipelineUpdating
            && !closeRequested && !closePromptOpen && closingDocument == null && targets.Length > 0
            && targets.All(target => ReferenceEquals(target.Document.SessionHistory, sessionHistory)
            && authoring?.Find(target.Document.Asset.Id) is { IsPublished: true } state && ReferenceEquals(state.Document, target.Document)
            && IsCurrentHierarchyContextOwner(target.Document)
            && target.Document.CanPasteHierarchyContent(text, target.Targets));
    private void RegisterHierarchyEntityRow(EntityHierarchyDocument owner, Guid id)
    {
        var target = HierarchyContentItem.Entity(owner, id);
        SelectionService.RegisterHierarchyRow(target);
        HierarchyController.SetKeyboardRowTarget(target);
    }
    private void RegisterHierarchyFolderRow(EntityHierarchyDocument owner, Guid? parent, string path)
    {
        var target = HierarchyContentItem.Folder(owner, parent, path);
        SelectionService.RegisterHierarchyRow(target);
        HierarchyController.SetKeyboardRowTarget(target);
    }
    private bool IsHierarchyFolderSelected(EntityHierarchyDocument owner, Guid? parent, string path)
        => SelectionService.IsContentSelected(HierarchyContentItem.Folder(owner, parent, path));
    private bool IsHierarchyEntityContentSelected(EntityHierarchyDocument owner, Guid id)
        => SelectionService.IsContentSelected(HierarchyContentItem.Entity(owner, id));
    private void QueueHierarchyFolderClick(EntityHierarchyDocument owner, Guid? parent, string path, bool toggle, bool range)
        => SelectionService.QueueHierarchyClick(HierarchyContentItem.Folder(owner, parent, path), toggle, range);
    private void QueueHierarchyEntityClick(EntityHierarchyDocument owner, Guid id, bool toggle, bool range)
        => SelectionService.QueueHierarchyClick(HierarchyContentItem.Entity(owner, id), toggle, range);
    private void EnsureHierarchyFolderSelected(EntityHierarchyDocument owner, Guid? parent, string path)
    {
        if (!IsHierarchyFolderSelected(owner, parent, path))
        {
            QueueHierarchyFolderClick(owner, parent, path, false, false);
            CompleteHierarchyContentSelection(activateOwner: false);
        }
        folderSelectionDocument = owner; selectedHierarchyFolder = (parent, path);
    }
    private void CompleteHierarchyContentSelection(bool activateOwner = true)
    {
        if (!SelectionService.HasPendingHierarchyClick && !SelectionService.HasPendingSceneRootClick) return;
        SelectionService.ValidatePendingHierarchySelection();
        engineViewport?.Picker?.Cancel("Hierarchy selection changed");
        FinishEntityRename(true); assetDocument?.EndEdit(); assetCommandContext = false;
        documents.CommitAll();
        if (SelectionService.HasPendingSceneRootClick)
        {
            SelectionService.CompleteSceneRootRows();
            selectedHierarchyFolder = null; folderSelectionDocument = null;
            return;
        }
        if (SelectionService.CompleteHierarchyRows() is not { } primary) return;
        if (activateOwner && primary.Document is SceneDocument scene && !ReferenceEquals(scene, document)
            && IsSceneLoaded(scene) && engineViewport?.IsPresentedScene(scene) == true) ActivateSceneRoot(scene);
        folderSelectionDocument = primary.Document;
        selectedHierarchyFolder = primary.FolderPath != null ? (primary.FolderOwner, primary.FolderPath) : null;
    }
}
