namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void SaveCurrentScene()
    {
        if (IsPackagePlatformUpdating) throw new InvalidOperationException("The package platforms are being updated.");
        if (IsPackageDependencyBusy) throw new InvalidOperationException("A project is already loading.");
        var active = document ?? throw new InvalidOperationException("No document is active.");
        var expectedWorkspace = workspace;
        var expectedHistory = SessionAuthoringHistory;
        void CheckSaveOwner()
        {
            if (!ReferenceEquals(document, active) || !ReferenceEquals(workspace, expectedWorkspace)
                || !ReferenceEquals(SessionAuthoringHistory, expectedHistory) || IsPackageDependencyBusy || IsPackagePlatformUpdating)
                throw new InvalidOperationException("The document changed while saving pending edits.");
        }
        ValidatePendingPropertyStrings();
        FinishEntityRename(true);
        CheckSaveOwner();
        active.CommitEdits(); CheckSaveOwner(); active.Save(); SynchronizeSavedGameSettingsOrientations();
        statusMessage = "Saved " + Path.GetFileName(active.Path);
    }
}
