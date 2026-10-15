using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool showDiscard;
    private bool documentReplacementSaving;
    private Action? afterDiscard;
    private Action? validateReplacementOwner;
    private void DrawDiscardDialog()
    {
        if (showDiscard) { Gui.OpenPopup("Unsaved changes"); showDiscard = false; }
        using var modal = ImGuiScopes.Modal("Unsaved changes", ImGuiWindowFlags.AlwaysAutoResize);
        if (!modal.DrawContents) return;
        if (afterDiscard == null) { Gui.CloseCurrentPopup(); return; }
        DrawSelectableText("##DiscardMessage", "Save changes before switching?");
        using (ImGuiScopes.Disabled(IsPackagePlatformUpdating || documentReplacementSaving))
        {
            var saveAndContinue = DrawNativeButton("Save and continue");
            if (Controls.Enabled) Controls.Record("replace/save");
            if (saveAndContinue)
            {
                Execute(SaveRequestedReplacement);
            }
            Gui.SameLine(); var discard = DrawNativeButton("Discard");
            if (Controls.Enabled) Controls.Record("replace/discard");
            if (discard) Execute(() => { ContinueRequestedReplacement(true); Gui.CloseCurrentPopup(); });
        }
        Gui.SameLine(); using var saving = ImGuiScopes.Disabled(documentReplacementSaving); var cancel = DrawNativeButton(EditorLocalization.Widget("Cancel", "Button"));
        if (Controls.Enabled) Controls.Record("replace/cancel");
        if (cancel) { Gui.CloseCurrentPopup(); ClearDocumentReplacementState(); }
    }
    private void SaveRequestedReplacement()
    {
        var intent = afterDiscard;
        var validate = validateReplacementOwner;
        if (intent == null || documentReplacementSaving) return;
        validate?.Invoke();
        documentReplacementSaving = true;
        void Complete()
        {
            documentReplacementSaving = false;
            if (!ReferenceEquals(afterDiscard, intent) || !ReferenceEquals(validateReplacementOwner, validate))
                throw new InvalidOperationException("The document replacement intent changed while saving.");
            validate?.Invoke();
            ContinueRequestedReplacement(false);
        }
        void Failed(Exception error)
        {
            documentReplacementSaving = false;
            if (ReferenceEquals(afterDiscard, intent)) showDiscard = true;
            statusMessage = "Save failed: " + error.Message;
        }
        try
        {
            if (saveAssetOnReplace) SaveAllDocuments(Complete, Failed);
            else { SaveCurrentScene(); Complete(); }
        }
        catch (Exception error) { Failed(error); throw; }
    }

    private bool saveAssetOnReplace;
    private bool replaceWorkspaceOnDiscard;
    private void ClearDocumentReplacementState()
    {
        afterDiscard = validateReplacementOwner = null; showDiscard = false;
        saveAssetOnReplace = replaceWorkspaceOnDiscard = false;
    }
    private void ContinueRequestedReplacement(bool discardAuthorized)
    {
        var action = afterDiscard;
        if (action == null) return;
        validateReplacementOwner?.Invoke();
        void CompleteReplacement()
        {
            try
            {
                action();
                if (ReferenceEquals(afterDiscard, action)) ClearDocumentReplacementState();
            }
            catch
            {
                if (ReferenceEquals(afterDiscard, action)) showDiscard = true;
                throw;
            }
        }
        if (discardAuthorized) AbandonPendingPropertyStrings();
        try
        {
            if (replaceWorkspaceOnDiscard) BeginWorkspaceRecoveryReplacement(CompleteReplacement, discardAuthorized,
                () => { if (ReferenceEquals(afterDiscard, action)) showDiscard = true; });
            else CompleteReplacement();
        }
        catch
        {
            if (ReferenceEquals(afterDiscard, action)) showDiscard = true;
            throw;
        }
    }
    private void RequestReplace(Action action, bool includeAssets = false, bool replaceWorkspace = false)
    {
        if (IsPackageMetadataSaving || IsPackageDependencyBusy || IsPackagePlatformUpdating || RecoveryTransitionBusy) throw new InvalidOperationException("A project transition is already running.");
        var expectedWorkspace = workspace;
        var expectedHistory = SessionAuthoringHistory;
        var expectedScene = document;
        var expectedAsset = assetDocument;
        var expectedCommandDocument = CommandDocument;
        void CheckTransitionOwner()
        {
            if (!ReferenceEquals(workspace, expectedWorkspace) || !ReferenceEquals(SessionAuthoringHistory, expectedHistory)
                || !ReferenceEquals(document, expectedScene) || !ReferenceEquals(assetDocument, expectedAsset)
                || !ReferenceEquals(CommandDocument, expectedCommandDocument)
                || IsPackageDependencyBusy || IsPackagePlatformUpdating)
                throw new InvalidOperationException("The project changed while preparing the document transition.");
        }
        void ReplaceCurrentOwner() { CheckTransitionOwner(); action(); }
        ValidatePendingPropertyStrings();
        FinishEntityRename(true);
        CheckTransitionOwner();
        document?.EndEdits();
        CheckTransitionOwner();
        assetDocument?.EndEdit();
        CheckTransitionOwner();
        if (includeAssets) documents.CommitAll();
        CheckTransitionOwner();
        if (includeAssets ? documents.HasDirtyDocuments || workspace?.HasPackageChanges == true : document?.IsDirty == true)
        {
            saveAssetOnReplace = includeAssets; replaceWorkspaceOnDiscard = replaceWorkspace;
            afterDiscard = ReplaceCurrentOwner; validateReplacementOwner = CheckTransitionOwner; showDiscard = true;
        }
        else
        {
            ClearDocumentReplacementState();
            Execute(() => { if (replaceWorkspace) BeginWorkspaceRecoveryReplacement(ReplaceCurrentOwner, false); else ReplaceCurrentOwner(); });
        }
    }
}
