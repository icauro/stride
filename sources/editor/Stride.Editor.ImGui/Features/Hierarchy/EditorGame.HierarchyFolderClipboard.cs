using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool HasHierarchyFolderClipboardSelection => SelectionService.SelectedSceneRoots.Count == 0 && SelectionService.HierarchyContent.Count == 1
        && SelectionService.HierarchyContent[0].FolderPath != null && folderSelectionDocument is { } source && IsCurrentHierarchyContextOwner(source)
        && ReferenceEquals(SelectionService.HierarchyContent[0].Document, source)
        && selectedHierarchyFolder is { } folder && SelectionService.HierarchyContent[0].FolderOwner == folder.Owner
        && string.Equals(SelectionService.HierarchyContent[0].FolderPath, folder.Path, StringComparison.OrdinalIgnoreCase)
        && source.GetHierarchyFolders(folder.Owner).Contains(folder.Path, StringComparer.OrdinalIgnoreCase);
    private bool CanCopyHierarchyClipboard => HasHierarchyFolderClipboardSelection
        ? folderSelectionDocument!.CanCopyHierarchyFolder(selectedHierarchyFolder!.Value.Owner, selectedHierarchyFolder.Value.Path) : CanCopyHierarchySelection;
    private bool CanCutHierarchyClipboard => HasHierarchyFolderClipboardSelection
        ? folderSelectionDocument!.CanCutHierarchyFolder(selectedHierarchyFolder!.Value.Owner, selectedHierarchyFolder.Value.Path) : CanCopyHierarchySelection && CanDeleteHierarchySelection;
    private bool CanDuplicateHierarchyClipboard => CanDuplicateHierarchySelection;
    private bool CanPasteHierarchyClipboard(string text)
    {
        if (projectLoading != null || assetPreviewBuild != null || IsAuthoringPipelineUpdating
            || closeRequested || closePromptOpen || closingDocument != null) return false;
        if (HasHierarchyFolderClipboardSelection)
            return folderSelectionDocument!.CanPasteHierarchyFolder(text, selectedHierarchyFolder!.Value.Owner, selectedHierarchyFolder.Value.Path);
        var targets = HierarchyPasteContentTargets();
        return CanPasteHierarchyDestinations(text, targets);
    }

    private void CopyHierarchyFolderSelection(bool cut)
    {
        var source = folderSelectionDocument!; var folder = selectedHierarchyFolder!.Value;
        if (cut ? !source.CanCutHierarchyFolder(folder.Owner, folder.Path) : !source.CanCopyHierarchyFolder(folder.Owner, folder.Path)) return;
        ValidatePendingPropertyStrings();
        FinishEntityRename(true); assetDocument?.EndEdit(); assetCommandContext = false;
        if (!IsCurrentHierarchyContextOwner(source) || !ReferenceEquals(folderSelectionDocument, source) || selectedHierarchyFolder is not { } currentFolder) return;
        folder = currentFolder;
        if (cut ? !source.CanCutHierarchyFolder(folder.Owner, folder.Path) : !source.CanCopyHierarchyFolder(folder.Owner, folder.Path)) return;
        var text = source.CopyHierarchyFolder(folder.Owner, folder.Path);
        if (text.Length > 16 * 1024 * 1024) throw new InvalidOperationException("The selected hierarchy is too large for the clipboard.");
        Gui.SetClipboardText(text);
        if (Gui.GetClipboardTextS() != text) throw new InvalidOperationException("The hierarchy folder could not be written to the clipboard.");
        if (cut)
        {
            engineViewport?.Picker?.Cancel("Cut entities");
            var emptyFolder = !source.HierarchyFolderHasEntities(folder.Owner, folder.Path);
            ApplyCompositeHierarchyCommand([source], "Cut selection", owner =>
            {
                owner.CutHierarchyFolder(folder.Owner, folder.Path);
                if (emptyFolder) owner.RemoveHierarchyFolders([(folder.Owner, folder.Path)]);
            });
            SelectionService.ClearHierarchyContent();
            selectedHierarchyFolder = null;
        }
    }

    private void PasteHierarchyFolderSelection(string text)
    {
        var target = folderSelectionDocument!; var folder = selectedHierarchyFolder!.Value;
        if (!target.CanPasteHierarchyFolder(text, folder.Owner, folder.Path)) return;
        ValidatePendingPropertyStrings();
        FinishEntityRename(true); assetDocument?.EndEdit(); assetCommandContext = false;
        if (!IsCurrentHierarchyContextOwner(target) || !ReferenceEquals(folderSelectionDocument, target) || selectedHierarchyFolder is not { } currentFolder) return;
        folder = currentFolder;
        if (!target.CanPasteHierarchyFolder(text, folder.Owner, folder.Path)) return;
        engineViewport?.Picker?.Cancel("Paste entities");
        ApplyCompositeHierarchyCommand([target], "Paste entities", owner => owner.PasteHierarchyFolder(text, folder.Owner, folder.Path));
        RevealHierarchyContext(target, null, folder.Owner, folder.Path);
    }

}
