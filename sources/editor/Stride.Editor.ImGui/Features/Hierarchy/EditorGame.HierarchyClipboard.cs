using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void CopyHierarchySelection(bool cut)
    {
        if (HasHierarchyFolderClipboardSelection) { CopyHierarchyFolderSelection(cut); return; }
        if (!CanCopyHierarchySelection || cut && !CanDeleteHierarchySelection) return;
        ValidatePendingPropertyStrings();
        FinishEntityRename(true); assetDocument?.EndEdit(); assetCommandContext = false;
        var folders = SelectionService.HierarchyContent.Where(item => item.FolderPath != null)
            .Select(item => (Document: item.Document, Owner: item.FolderOwner, Path: item.FolderPath!)).ToArray();
        var copiedFolders = SelectionService.HierarchyClipboardFolders
            .Select(item => (Document: item.Document, Owner: item.FolderOwner, Path: item.FolderPath!)).ToArray();
        var owners = SelectedHierarchyDocuments.Concat(folders.Select(folder => folder.Document)).Distinct().ToArray();
        var text = EntityHierarchyDocument.CopySelectedEntities(owners, copiedFolders);
        if (text.Length > 16 * 1024 * 1024) throw new InvalidOperationException("The selected hierarchy is too large for the clipboard.");
        Gui.SetClipboardText(text);
        if (Gui.GetClipboardTextS() != text) throw new InvalidOperationException("The entity hierarchy could not be written to the clipboard.");
        if (cut)
        {
            engineViewport?.Picker?.Cancel("Cut entities");
            var emptyFolders = folders.Where(folder => !folder.Document.HierarchyFolderHasEntities(folder.Owner, folder.Path)).ToArray();
            var cutOwners = owners.Concat(folders.Select(folder => folder.Document)).Distinct().ToArray();
            ApplyCompositeHierarchyCommand(cutOwners, "Cut selection", owner =>
            {
                if (owner.SelectedCount > 0) owner.CutSelectedEntities();
                owner.RemoveHierarchyFolders(emptyFolders.Where(folder => ReferenceEquals(folder.Document, owner)).Select(folder => (folder.Owner, folder.Path)));
            });
            SelectionService.ClearHierarchyContent(); selectedHierarchyFolder = null;
        }
    }

    private void PasteHierarchySelection(bool asRoot)
    {
        if (document == null || projectLoading != null || assetPreviewBuild != null || IsAuthoringPipelineUpdating
            || closeRequested || closePromptOpen || closingDocument != null) return;
        var text = Gui.GetClipboardTextS() ?? "";
        if (asRoot && document != null && text.StartsWith("# StrideHierarchyFoldersV1:", StringComparison.Ordinal))
        {
            if (!document.CanPasteHierarchyContent(text, [(null, "")])) return;
            FinishEntityRename(true);
            if (!document.CanPasteHierarchyContent(text, [(null, "")])) return;
            ApplyCompositeHierarchyCommand([document], "Paste selection", owner => owner.PasteHierarchyContent(text, [(null, "")]));
            return;
        }
        if (!asRoot && HasHierarchyFolderClipboardSelection) { PasteHierarchyFolderSelection(text); return; }
        if (!asRoot && (SelectionService.HierarchyContent.Count > 0 || SelectionService.SelectedSceneRoots.Count > 0))
        {
            var targets = HierarchyPasteContentTargets();
            if (!CanPasteHierarchyDestinations(text, targets)) return;
            FinishEntityRename(true); assetDocument?.EndEdit(); assetCommandContext = false;
            targets = HierarchyPasteContentTargets();
            if (!CanPasteHierarchyDestinations(text, targets)) return;
            engineViewport?.Picker?.Cancel("Paste entities");
            ApplyCompositeHierarchyCommand(targets.Select(target => target.Document).ToArray(), "Paste entities", owner =>
                owner.PasteHierarchyContent(text, targets.Single(target => ReferenceEquals(target.Document, owner)).Targets));
            return;
        }
        var owners = asRoot ? new[] { document } : SelectedHierarchyDocuments.ToArray();
        if (owners.Length == 0 || owners.Any(owner => !owner.CanPasteEntities(text, asRoot))) return;
        FinishEntityRename(true); assetDocument?.EndEdit(); assetCommandContext = false;
        if (owners.Any(owner => !owner.CanPasteEntities(text, asRoot))) return;
        engineViewport?.Picker?.Cancel("Paste entities");
        ApplyCompositeHierarchyCommand(owners, "Paste entities", owner => owner.PasteEntities(text, asRoot));
    }

    private void DrawHierarchyClipboardMenu()
    {
        if (DrawNativeMenuItem(EditorLocalization.Widget("Duplicate", "Menu"), "Ctrl+D", false, CanDuplicateHierarchyClipboard)) Execute(DuplicateHierarchySelection);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Copy", "Menu"), "Ctrl+C", false, CanCopyHierarchyClipboard)) Execute(() => CopyHierarchySelection(false));
        if (DrawNativeMenuItem(EditorLocalization.Widget("Cut", "Menu"), "Ctrl+X", false, CanCutHierarchyClipboard)) Execute(() => CopyHierarchySelection(true));
        var text = Gui.GetClipboardTextS() ?? "";
        if (DrawNativeMenuItem(EditorLocalization.Widget("Paste", "Menu"), "Ctrl+V", false, CanPasteHierarchyClipboard(text))) Execute(() => PasteHierarchySelection(false));
        if (DrawNativeMenuItem("Paste as root", "Ctrl+Shift+V", false, document?.CanPasteEntities(text, true) == true)) Execute(() => PasteHierarchySelection(true));
    }

    private void HandleHierarchyClipboardShortcuts()
    {
        if (projectLoading != null || assetPreviewBuild != null || IsAuthoringPipelineUpdating || closeRequested || closePromptOpen || closingDocument != null
            || CommandDocument is not EntityHierarchyDocument || !ReferenceEquals(CommandDocument, document)
            || !Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) || Gui.IsAnyItemActive() || Gui.GetIO().WantTextInput
            || Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel) || !Gui.GetIO().KeyCtrl) return;
        if (Gui.IsKeyPressed(ImGuiKey.C)) Execute(() => CopyHierarchySelection(false));
        else if (Gui.IsKeyPressed(ImGuiKey.X)) Execute(() => CopyHierarchySelection(true));
        else if (Gui.IsKeyPressed(ImGuiKey.V)) Execute(() => PasteHierarchySelection(Gui.GetIO().KeyShift));
        else if (Gui.IsKeyPressed(ImGuiKey.D)) Execute(DuplicateHierarchySelection);
    }

    private void DuplicateHierarchySelection()
    {
        if (!CanDuplicateHierarchySelection) return;
        // Native DuplicateSelection consumes the actual SelectedItems projection, not folder
        // view objects. Empty folders contribute no entity roots and must not author metadata.
        var owners = SelectedHierarchyDocuments.ToArray();
        if (owners.Length == 0) return;
        FinishEntityRename(true); engineViewport?.Picker?.Cancel("Duplicate entities");
        ApplyCompositeHierarchyCommand(owners, "Duplicate selection", owner => owner.DuplicateSelectedEntities());
        SelectionService.ClearHierarchyContent(); selectedHierarchyFolder = null;
        var primaryOwner = CompositeHierarchyPrimary?.Document ?? owners.FirstOrDefault(owner => owner.SelectedCount > 0);
        if (primaryOwner != null) RevealHierarchyContext(primaryOwner, primaryOwner.Selected?.Id, primaryOwner.Selected?.Transform.Parent?.Entity.Id);
    }
    private void ApplyCompositeHierarchyCommand(EntityHierarchyDocument[] owners, string name, Action<EntityHierarchyDocument> apply,
        bool allowRetainedDragSources = false, HierarchyMoveSelectionPlan? selectionFixup = null)
    {
        if (owners.Length == 0) return;
        if (sessionHistory == null && owners.Length == 1) { ValidatePendingPropertyStrings(); documents.CommitAll(); apply(owners[0]); selectionFixup?.ApplyInitial(); return; }
        if (selectionFixup != null && sessionHistory?.IsCapturingOperations == true)
            throw new InvalidOperationException("A hierarchy presentation fixup must wrap its own actual operation group.");
        if (owners.Any(owner => !ReferenceEquals(owner.SessionHistory, sessionHistory)
            || !ReferenceEquals(authoring?.Find(owner.Asset.Id)?.Document, owner)))
            throw new InvalidOperationException("The selected scene authored generation changed.");
        var primaryOwner = CompositeHierarchyPrimary?.Document ?? owners[0];
        ValidatePendingPropertyStrings();
        documents.CommitAll();
        if (owners.Any(owner => !(allowRetainedDragSources ? IsCurrentHierarchyDragSource(owner) : IsCurrentHierarchyContextOwner(owner))))
            throw new InvalidOperationException("The selected hierarchy owner changed.");
        using var transaction = sessionHistory!.CreateTransaction(name);
        if (sessionHistory.IsCapturingOperations) foreach (var owner in owners) apply(owner);
        else
        {
            try
            {
                using var capture = sessionHistory.CaptureOperationsAtomic(() =>
                {
                    foreach (var owner in owners) apply(owner);
                    selectionFixup?.ApplyInitial();
                });
                if (selectionFixup == null) capture.Publish(sessionHistory, name);
                else capture.Publish(sessionHistory, name, selectionFixup.Wrap);
            }
            catch (Exception failure)
            {
                try { selectionFixup?.RestoreInitial(); }
                catch (Exception recovery) { throw new AggregateException("Hierarchy move and selection restoration failed.", failure, recovery); }
                throw;
            }
        }
        if (selectionFixup == null && primaryOwner.Selected is { } selected) compositeHierarchyPrimary = (primaryOwner, selected.Id);
    }
}
