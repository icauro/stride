namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DetachGraphicsRecoveryJobs()
    {
        CancelSceneCameraNavigation();
        ClearPropertyPathDropTargets();
        // Actual view settings are retained in the recovery snapshots. Preparing
        // their GPU retirement must not publish an authoring history operation.
        _ = BeginEditorGpuRetirementAsync(retireScenes: true, retireFonts: true, persistSceneSettings: false);
        CancelSceneLightProbeCommands();
        projectCreationPanel?.Cancel();
        StopEditorExtensionCommands();
        StopAssetImportUpdate(); StopCompositorSettingsUpdate(); StopFontSettingsUpdate();
        StopProjectBuildOperations(); StopVisualScriptCompilation(); StopScriptSemantics(); StopAddedSolutionProjects();
        StopActiveProjectSwitch();
        StopPackagePlatformUpdate();

        DetachGraphicsRecoveryBuild(textureSettingsBuild, textureSettingsCancellation, CompiledTextureSettingsBuild.ReleaseOutput);
        textureSettingsBuild = null; textureSettingsCancellation = null; compilingTextureSettings = null;
        compilingTextureSettingsSnapshot = null; compilingTextureSettingsPath = null; textureSettingsBuildState = null;

        DetachGraphicsRecoveryBuild(spriteSheetBuild, spriteSheetCancellation, CompiledSpriteSheetBuild.ReleaseOutput);
        spriteSheetBuild = null; spriteSheetCancellation = null; compilingSpriteDocument = null; compilingSpritePath = null;
        spriteSheetBuildState = null;

        DetachGraphicsRecoveryBuild(importSettingsBuild, importSettingsCancellation, CompiledImportAssetBuild.ReleaseOutput);
        importSettingsBuild = null; importSettingsCancellation = null; importCompilationRequest = null;

        DetachGraphicsRecoveryBuild(mediaPreviewBuild, mediaPreviewCancellation, CompiledMediaBuild.ReleaseOutput);
        mediaPreviewBuild = null; mediaPreviewCancellation = null; compilingMediaSnapshot = null;

        DetachGraphicsRecoveryBuild(skyboxBuild, skyboxCancellation, CompiledSkyboxBuild.ReleaseOutput, skyboxOwnerLease);
        skyboxBuild = null; skyboxCancellation = null; skyboxOwnerLease = null;
        compilingSkyboxDocument = null; compilingSkyboxRevision = null;

        DetachGraphicsRecoveryBuild(assetPreviewBuild, assetPreviewCancellation, ReleaseGraphicsRecoveryProjectOutput);
        assetPreviewBuild = null; assetPreviewCancellation = null; assetPreviewProgress = null;

        var loading = projectLoading; var cancellation = projectCancellation;
        projectLoading = null; projectCancellation = null; projectLoadProgress = null; cancellation?.Cancel();
        ClearNativeMigrationLoadingState();
        if (loading == null) cancellation?.Dispose();
        else
        {
            Interlocked.Increment(ref graphicsRecoveryDetachedBuilds);
            _ = loading.ContinueWith(completed =>
            {
                try
                {
                    if (completed.Status == TaskStatus.RanToCompletion)
                        graphicsRecoveryCleanup.Enqueue(completed.Result.ReleaseUnadopted);
                    else if (completed.IsFaulted) _ = completed.Exception;
                }
                finally { cancellation?.Dispose(); Interlocked.Decrement(ref graphicsRecoveryDetachedBuilds); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            AbortRecoveryProjectReplacement();
        }
        // Preserve source-tool results as CPU state; they cannot publish edits while the
        // host is suspended, and their later guarded apply still uses canonical revisions.
    }

    private static void ReleaseGraphicsRecoveryProjectOutput(string output) => OwnedProjectBuildOutput.Release(output);
}
