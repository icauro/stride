namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool IsPackageDependencyBusy => IsPackageDependencyUpdating || IsPackageDependencyChecking;
    private bool IsAuthoringPipelineUpdatingExceptEditorExtensionCommandAndSceneLightProbe => IsEditorGpuRetirementPending || IsEditorGraphicsRecovering || IsActiveProjectSwitching || IsAddingSolutionProjects || IsPackagePlatformUpdating || IsPackageMetadataSaving || IsProjectCreationUpdating || IsCompositorSettingsUpdating || RecoveryTransitionBusy || IsAssetImportUpdating || IsImportSettingsUpdating || IsTextureSettingsUpdating || IsSkyboxSettingsUpdating || IsFontSettingsUpdating || IsMediaPreviewUpdating || IsCatalogPreviewUpdating || IsExternalAssetPreviewUpdating || IsExternalAssetPreviewDrainPending || IsPackageDependencyBusy || HasPendingSpriteSourceEdits;
    private bool IsAuthoringPipelineUpdatingExceptEditorExtensionCommand => IsSceneLightProbeBusy || IsAuthoringPipelineUpdatingExceptEditorExtensionCommandAndSceneLightProbe;
    private bool IsAuthoringPipelineUpdatingExceptSceneLightProbe => IsEditorExtensionCommandRunning || IsAuthoringPipelineUpdatingExceptEditorExtensionCommandAndSceneLightProbe;
    private bool IsAuthoringPipelineUpdating => IsEditorExtensionCommandRunning || IsAuthoringPipelineUpdatingExceptEditorExtensionCommand;
    private bool CanStartAuthoringPreviewUpdate => workspace != null && projectContent != null && !pumpingInactiveAuthoring && !IsAuthoringPipelineUpdating
        && projectLoading == null && assetPreviewBuild == null && !IsUserProjectBuilding
        && !closeRequested && !closePromptOpen && closingDocument == null && !exitApproved;
}
