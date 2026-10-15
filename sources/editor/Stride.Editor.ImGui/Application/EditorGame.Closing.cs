using System.ComponentModel;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool closeRequested, openClosePopup, closePromptOpen, exitApproved;
    private bool terminalCloseRetirementStarted;
    private string? closeError;
    private readonly Dictionary<Guid, bool> closeSaveSelection = [];
    private bool savePackageOnClose;
    private bool closingPackageSave;
    private IEnumerable<(Guid Id, IEditorDocument Document)> ClosingDocuments => authoring != null
        ? authoring.PublishedStates.Where(state => state.Document is not PackageMetadataDocument).Select(state => (state.Generation, state.Document))
        : documents.Entries.Select(entry => (entry.Id, entry.Document));
    internal bool ClosePromptOpen => closePromptOpen;
    internal event Action? CloseApproved;
    internal event Action? CloseApprovalPrepared;
    private void OnEditorWindowClosing(object? sender, EventArgs args)
    {
        if (smokeTest || exitApproved || args is not CancelEventArgs cancellable) return;
        cancellable.Cancel = true; Execute(RequestEditorClose);
    }
    private void RequestEditorClose()
    {
        CancelSceneCameraNavigation();
        if (exitApproved || closePromptOpen || closingDocument != null || RecoveryTransitionBusy) return;
        ValidatePendingPropertyStrings();
        closeRequested = true; projectCancellation?.Cancel(); assetPreviewCancellation?.Cancel();
        CancelSceneLightProbeCommands();
        projectCreationPanel?.Cancel();
        StopActiveProjectSwitch(); StopProjectIdeOpen();
        StopEditorExtensionCommands();
        textureSettingsCancellation?.Cancel();
        StopAddedSolutionProjects(); compositorCancellation?.Cancel();
        StopPackagePlatformUpdate();
        assetImportCancellation?.Cancel(); importSettingsCancellation?.Cancel();
        mediaPreviewCancellation?.Cancel(); fontCancellation?.Cancel();
    }
    private void PumpEditorClosing()
    {
        if (DiagnosticCompletionActive) return;
        if (!closeRequested || projectLoading != null || assetPreviewBuild != null || IsAuthoringPipelineUpdating || RecoveryTransitionBusy) return;
        closeRequested = false; FinishEntityRename(true); document?.EndEdits(); assetDocument?.EndEdit();
        documents.CommitAll();
        if (documents.HasDirtyDocuments || workspace?.HasPackageChanges == true)
        {
            savePackageOnClose = true;
            closeSaveSelection.Clear();
            foreach (var entry in ClosingDocuments.Where(entry => entry.Document.IsDirty)) closeSaveSelection.Add(entry.Id, entry.Document.CanSave);
            closeError = null; closePromptOpen = openClosePopup = true;
        }
        else Execute(ApproveEditorClose);
    }
    private void ApproveEditorClose() => ApproveEditorClose(false);
    private void ApproveEditorClose(bool discardAuthorized)
    {
        CloseApprovalPrepared?.Invoke();
        PrepareApprovedCloseDiagnosticCapture();
        BeginRecoveryProtectedClose(() =>
        {
            if (discardAuthorized) RevertWorkspaceMetadata();
            SaveSceneCameraSettings(); engineViewport?.ClearScene(); ClearDocumentSession();
        }, () =>
        {
            BeginTerminalCloseRetirement(() =>
            {
                CloseApproved?.Invoke();
                PublishApprovedCloseDiagnosticResult();
                Exit();
            }, error =>
            {
                closeError = "Terminal resource retirement failed: " + error.Message;
                ReportRecoveryTransitionFailure(error);
            });
        }, discardAuthorized, error =>
        {
            closeError = "Recovery checkpoint failed: " + error.Message;
            closePromptOpen = openClosePopup = true; ReportRecoveryTransitionFailure(error);
        });
    }
    private void BeginTerminalCloseRetirement(Action completed, Action<Exception> failed)
    {
        // Protected graph teardown can enqueue view/target descendants. Their
        // actual disposal and last GPU work need a fresh certificate before Exit.
        terminalCloseRetirementStarted = true;
        try
        {
            CloseUIEditor(); CloseCurveEditor();
            CloseLegacyAssetPreviews(); CloseCatalogAssetPreviews();
            ClearTemplateImages(); ClearAssetThumbnails();
        }
        catch (Exception error) { failed(error); return; }
        RecoveryTransitions.Run(() => BeginEditorGpuRetirementAsync(retireScenes: true, retireFonts: true), () =>
        {
            EnsureSceneCpuRetirementCompleted(); EnsureEditorGpuRetirementCompleted(); EnsureInstalledFontGraphicsRetired();
            if (retiredEditorPreviews.Count != 0)
                throw new InvalidOperationException("Terminal close still owns native preview resources.");
            exitApproved = true; closePromptOpen = false;
            completed();
        }, failed);
    }
    private void DrawEditorCloseDialog()
    {
        var confirmationTitle = WpfMessageDialogTitle("CloseEditorConfirmation");
        if (openClosePopup) { Gui.OpenPopup(confirmationTitle); openClosePopup = false; }
        Gui.SetNextWindowSize(UiScale(600, 0), ImGuiCond.Appearing);
        using var modal = ImGuiScopes.Modal(confirmationTitle, ImGuiWindowFlags.AlwaysAutoResize);
        TraceWorkflowUiAction("modal/editor-close", modal.DrawContents);
        if (!modal.DrawContents) return;
        if (!closePromptOpen) { Gui.CloseCurrentPopup(); return; }
        using var savePending = ImGuiScopes.Disabled(closingPackageSave);
        Gui.PushTextWrapPos(0);
        try
        {
            DrawSelectableText("##CloseMessage", "Save unsaved documents before closing?");
            foreach (var entry in ClosingDocuments.Where(entry => entry.Document.IsDirty))
            {
                Gui.PushID(entry.Id.ToString());
                var save = closeSaveSelection.GetValueOrDefault(entry.Id);
                Gui.BeginDisabled(!entry.Document.CanSave);
                if (Gui.Checkbox(EditorLocalization.Widget("Save", "Button"), ref save)) closeSaveSelection[entry.Id] = save;
                Gui.EndDisabled(); Gui.SameLine(); DrawSelectableText("##DocumentPath", entry.Document.Path); Gui.PopID();
            }
            if (workspace?.HasPackageChanges == true)
            {
                Gui.Checkbox("Save package / solution changes", ref savePackageOnClose);
                DrawSelectableText("##ClosePackagePath", workspace.OpenPath);
            }
            if (closeError != null) DrawSelectableText("##CloseError", closeError);
        }
        finally { Gui.PopTextWrapPos(); }
        Gui.Separator();
        var saveCloseClicked = DrawNativeButton(EditorLocalization.Widget("Save and close", "Button"));
        RecordNativeCloseButton("save", saveCloseClicked);
        TraceWorkflowUiAction("close/save", saveCloseClicked);
        if (saveCloseClicked)
        {
            try
            {
                ValidatePendingPropertyStrings();
                FinishEntityRename(true); documents.CommitAll();
                foreach (var entry in ClosingDocuments.Where(entry => entry.Document.IsDirty && closeSaveSelection.GetValueOrDefault(entry.Id))) entry.Document.Save();
                SynchronizeSavedGameSettingsOrientations();
                if (savePackageOnClose && workspace?.HasPackageChanges == true)
                {
                    var source = workspace; var history = SessionAuthoringHistory;
                    closingPackageSave = true;
                    RequestPackageMetadataSave(() =>
                    {
                        closingPackageSave = false;
                        if (!closePromptOpen || !ReferenceEquals(workspace, source) || !ReferenceEquals(SessionAuthoringHistory, history))
                            throw new InvalidOperationException("The close intent changed while saving.");
                        ApproveEditorClose(true);
                    }, error => { closingPackageSave = false; closeError = "Save failed: " + error.Message; });
                }
                else { Gui.CloseCurrentPopup(); ApproveEditorClose(true); }
            }
            catch (Exception error) { closingPackageSave = false; closeError = "Save failed: " + error.Message; statusMessage = error.Message; Console.Error.WriteLine(error); }
        }
        if (Controls.Enabled) Controls.Record("close/save"); Gui.SameLine();
        var discardCloseClicked = DrawNativeButton("Discard and close");
        RecordNativeCloseButton("discard", discardCloseClicked);
        TraceWorkflowUiAction("close/discard", discardCloseClicked);
        if (discardCloseClicked)
        {
            try { Gui.CloseCurrentPopup(); ApproveEditorClose(true); }
            catch (Exception error) { closeError = "Discard failed: " + error.Message; statusMessage = error.Message; }
        }
        if (Controls.Enabled) Controls.Record("close/discard"); Gui.SameLine();
        var cancelCloseClicked = DrawNativeButton(EditorLocalization.Widget("Cancel", "Button"));
        RecordNativeCloseButton("cancel", cancelCloseClicked);
        TraceWorkflowUiAction("close/cancel", cancelCloseClicked);
        if (cancelCloseClicked) { Gui.CloseCurrentPopup(); closePromptOpen = false; closeError = null; }
        if (Controls.Enabled) Controls.Record("close/cancel");
    }
}
