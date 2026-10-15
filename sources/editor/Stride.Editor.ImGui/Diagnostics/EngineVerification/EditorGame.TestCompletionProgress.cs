using System.Text.Json;
using Stride.Engine;
using Stride.Engine.Processors;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private string? diagnosticCompletionTracePath, diagnosticCompletionTraceError;
    private long diagnosticCompletionTraceAt;
    internal void ConfigureDiagnosticCompletionTrace(string path)
    {
        diagnosticCompletionTracePath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(diagnosticCompletionTracePath)!);
    }

    // Read only: no native GPU polls, owner drains, scheduler runs or readiness mutation.
    internal object CaptureDiagnosticCompletionProgress()
    {
        var retirement = editorGpuRetirement;
        return new
        {
            schema = 1, utc = DateTimeOffset.UtcNow, process = Environment.ProcessId,
            completion = diagnosticCompletion == null ? null : new
            {
                diagnosticCompletion.CancellationRequested, diagnosticCompletion.TransitionStarted,
                diagnosticCompletion.Finished, diagnosticCompletion.Passed, diagnosticCompletion.Failure,
            },
            recoveryBusy = RecoveryTransitionBusy, authoringBusy = IsAuthoringPipelineUpdating,
            projectLoad = projectLoading?.Status.ToString(), previewBuild = assetPreviewBuild?.Status.ToString(),
            closeRequested, exitApproved, graphicsRecovering = IsEditorGraphicsRecovering,
            gpu = retirement == null ? null : new
            {
                stage = retirement.Stage.ToString(), retirement.Error, retirement.RetireScenes, retirement.RetireFonts,
                retirement.FontsPrepared, markerPresent = retirement.Marker != null, deferredMarkers = retirement.DeferredMarkers.Count,
                retryAt = retirement.RetryAt, glyphDrain = retirement.GlyphDrain.Status.ToString(),
                retirement.ResourceRevision, resourceRevisionNow = retiredEditorPreviewRevision,
                retirement.ActionRevision, actionRevisionNow = externalPreviewActionRevision,
                sceneOwners = retirement.Scenes.Select(view => view.CaptureRetirementVerificationState()).ToArray(),
            },
            gpuCertified = editorGpuRetirementCertified,
            external = new { actions = externalPreviewActions.Count, drain = externalPreviewDrain.Status.ToString(),
                activePreview = externalPreview != null, retired = retiredExternalPreviews.Count,
                initializationPending = retiredExternalPreviews.Count(owner => owner.Initialization?.IsCompleted == false),
                inputReleasePending = retiredExternalPreviews.Count(owner => owner.InputReleasePending),
                updating = IsExternalAssetPreviewUpdating, drainPending = IsExternalAssetPreviewDrainPending },
            componentImageDrain = componentImageRetirement.Status.ToString(),
            sprite = new { sourceDraining = IsSpriteSourceWorkDraining, pendingSourceEdits = HasPendingSpriteSourceEdits },
            thumbnails = new { draining = IsAssetThumbnailServiceDraining, retiredServices = retiredAssetThumbnailServices.Count },
            extensions = new { commandRunning = IsEditorExtensionCommandRunning,
                ownedAssetEditors = sessionHistory?.Extensions?.HasOwnedAssetEditors == true },
            retiredPreviewOwners = retiredEditorPreviews.Count,
            retiredPreviewCpuDrains = retiredEditorPreviews.Count(owner => owner.Preview is IEditorResourceRetirementDrain drain
                && !drain.IsRetirementDrainComplete),
            activeScene = engineViewport?.CaptureRetirementVerificationState(),
            generation = new { project = projectCreationPanel?.IsGenerating == true, addedProject = addedProjectCreation?.IsGenerating == true },
            busy = new { platform = IsPackagePlatformUpdating, addingProjects = IsAddingSolutionProjects,
                compositor = IsCompositorSettingsUpdating, imports = IsAssetImportUpdating, importSettings = IsImportSettingsUpdating,
                texture = IsTextureSettingsUpdating, skybox = IsSkyboxSettingsUpdating, font = IsFontSettingsUpdating,
                media = IsMediaPreviewUpdating, catalog = IsCatalogPreviewUpdating, dependency = IsPackageDependencyBusy,
                projectSwitch = IsActiveProjectSwitching, sceneLightProbe = IsSceneLightProbeBusy },
            traceError = diagnosticCompletionTraceError,
            gameSettings = CaptureGameSettingsVerificationIdentity(),
        };
    }

    private void WriteDiagnosticCompletionProgress(bool force = false)
    {
        if (diagnosticCompletionTracePath == null || !force && Environment.TickCount64 < diagnosticCompletionTraceAt) return;
        diagnosticCompletionTraceAt = Environment.TickCount64 + 2000;
        try
        {
            File.AppendAllText(diagnosticCompletionTracePath, JsonSerializer.Serialize(CaptureDiagnosticCompletionProgress()) + Environment.NewLine);
        }
        catch (Exception error)
        {
            diagnosticCompletionTraceError = error.ToString();
            Console.Error.WriteLine("Test completion inspection failed: " + error.Message);
        }
    }
}

internal sealed partial class EngineViewport
{
    internal object CaptureRetirementVerificationState()
    {
        object InspectAuthority(SceneViewportAuthority authority)
        {
            try
            {
                var scheduler = authority.IsReleased ? null : authority.Services.GetService<ScriptSystem>()?.Scheduler;
                var threads = scheduler?.MicroThreads.ToArray();
                return new { authority.IsStopping, authority.IsShutdownComplete, authority.IsReleased,
                    schedulerThreads = threads?.Length ?? 0, liveSchedulerThreads = threads?.Count(thread => !thread.IsOver) ?? 0 };
            }
            catch (Exception error)
            {
                return new { authority.IsStopping, authority.IsShutdownComplete, authority.IsReleased,
                    inspectionError = error.Message };
            }
        }
        return new
        {
            CompiledSceneUrl, runtimeEntityCount = runtimeEntities.Count,
            cpuDrainComplete = IsSceneRetirementDrainComplete,
            currentAuthority = sceneAuthority == null ? null : InspectAuthority(sceneAuthority),
            drainingAuthorities = drainingAuthorities.Select(InspectAuthority).ToArray(),
            sceneResources = sceneResourceOwners.Count, targetResources = targetResourceOwners.Count,
            targetPresent = Target != null,
        };
    }
}
