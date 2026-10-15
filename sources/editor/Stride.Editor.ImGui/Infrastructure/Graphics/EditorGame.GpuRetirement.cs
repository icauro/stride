using Stride.Graphics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private enum GpuRetirementStage { Initial, Owners, Resources, Final }
    private sealed class EditorGpuRetirement(GraphicsDevice device)
    {
        internal readonly GraphicsDevice Device = device;
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<GraphicsGpuCompletion> DeferredMarkers = [];
        internal GraphicsGpuCompletion? Marker;
        internal GpuRetirementStage Stage;
        internal long RetryAt;
        internal string? Error;
        internal Task GlyphDrain = Task.CompletedTask;
        internal long ResourceRevision, ActionRevision;
        internal bool RetireScenes, RetireFonts, FontsPrepared;
        internal readonly List<EngineViewport> Scenes = [];
    }
    private EditorGpuRetirement? editorGpuRetirement;
    private bool editorGpuRetirementCertified;
    private GraphicsDevice? editorGpuRetirementCertifiedDevice;
    private long editorGpuRetirementCertifiedResourceRevision, editorGpuRetirementCertifiedActionRevision;
    private GraphicsDevice? protectedEditorResourceDisposalDevice;
    private bool IsEditorGpuRetirementPending => editorGpuRetirement != null;
    private bool IsEditorGpuRetirementFinalizing => terminalCloseRetirementStarted || editorGpuRetirement is { Stage: not GpuRetirementStage.Initial }
        || editorGpuRetirementCertified && (RecoveryTransitionBusy || closeRequested || exitApproved || IsEditorGraphicsRecovering);
    private long retiredEditorPreviewRevision, externalPreviewActionRevision;

    // Uses the existing transition coordinator's awaited barrier. No independent
    // project/recovery transition or synchronous async-provider wait is created.
    private Task BeginEditorGpuRetirementAsync(bool retireScenes = false, bool retireFonts = false, bool persistSceneSettings = true)
    {
        if (editorGpuRetirement is { } active)
        {
            if (retireScenes && !active.RetireScenes)
            { if (persistSceneSettings) SaveSceneDocumentViewSettingsBeforeRetirement(); EnableSceneRetirement(active); }
            if (retireFonts && !active.RetireFonts) EnableFontRetirement(active);
            return active.Completion.Task;
        }
        if (retireScenes && persistSceneSettings) SaveSceneDocumentViewSettingsBeforeRetirement();
        var retirement = new EditorGpuRetirement(GraphicsDevice);
        editorGpuRetirementCertified = false;
        editorGpuRetirementCertifiedDevice = null;
        editorGpuRetirement = retirement;
        if (retireScenes) EnableSceneRetirement(retirement);
        if (retireFonts) EnableFontRetirement(retirement);
        CancelSceneLightProbeCommands();
        icons?.SetComponentImageRetirementBlocked(true);
        BeginExternalAssetPreviewDrain();
        _ = DrainEditorComponentImagesAsync();
        retirement.GlyphDrain = BeginScriptGlyphImageDrain();
        return retirement.Completion.Task;
    }

    private void EnableSceneRetirement(EditorGpuRetirement retirement)
    {
        retirement.RetireScenes = true;
        retirement.Scenes.AddRange(LiveSceneDocumentViewports);
        if (engineViewport != null && !retirement.Scenes.Contains(engineViewport)) retirement.Scenes.Add(engineViewport);
        foreach (var view in retirement.Scenes) _ = view.BeginSceneRetirementDrain();
        DeferEditorGpuMarker(retirement);
        retirement.Stage = GpuRetirementStage.Initial;
    }

    private bool PumpSceneAndResourceRetirementDrains(EditorGpuRetirement retirement)
    {
        var complete = true;
        foreach (var view in retirement.Scenes)
        {
            if (view.IsSceneRetirementDrainComplete) continue;
            DeferEditorGpuMarker(retirement);
            if (!view.PumpSceneRetirementDrain()) complete = false;
        }
        foreach (var owner in retiredEditorPreviews.ToArray())
        {
            if (owner.Preview is not IEditorResourceRetirementDrain drain || drain.IsRetirementDrainComplete) continue;
            DeferEditorGpuMarker(retirement);
            if (!drain.PumpRetirementDrain()) complete = false;
        }
        return complete;
    }

    // true means an actual submitted-frame boundary or an inactive boundary with
    // no recording commands remaining. Normal Update polls with false. Callers
    // must not subsequently render the resources this transition will tear down.
    private void PumpEditorGpuRetirement(bool submittedBoundary = false)
    {
        if (editorGpuRetirement is not { } retirement || Environment.TickCount64 < retirement.RetryAt) return;
        try
        {
            if (!ReferenceEquals(retirement.Device, GraphicsDevice))
                throw new InvalidOperationException("The graphics device changed before its retirement certificate completed.");
            if (retirement.Stage == GpuRetirementStage.Initial)
            {
                if (retirement.RetireFonts && !retirement.FontsPrepared) EnableFontRetirement(retirement);
                if (!PumpSceneAndResourceRetirementDrains(retirement)) return;
                // Cancellation does not finish initialization. Its last owner
                // GPU callback must precede the initial completion marker.
                if (IsEditorExtensionCommandRunning || !externalPreviewActions.IsEmpty
                    || retiredExternalPreviews.Any(owner => owner.Initialization?.IsCompleted == false || owner.InputReleasePending)) return;
                if (!PollEditorGpuRetirementMarker(retirement, submittedBoundary)) return;
                icons?.SetComponentImageRetirementBlocked(false);
                BeginExternalAssetPreviewDrain(graphicsQuiesced: true);
                _ = DrainEditorComponentImagesAsync(graphicsQuiesced: true);
                retirement.Stage = GpuRetirementStage.Owners;
            }
            if (retirement.Stage == GpuRetirementStage.Owners)
            {
                BeginSpriteSourceWorkDrain();
                if (retirement.RetireScenes)
                    foreach (var view in retirement.Scenes) view.ClearScene();
                BeginAssetThumbnailServiceDrain();
                PumpRetiredAssetThumbnailServices();
                sessionHistory?.Extensions?.DrainOwnedAssetEditors();
                if (!externalPreviewDrain.IsCompleted || externalPreview != null || retiredExternalPreviews.Count != 0
                    || !componentImageRetirement.IsCompleted || !retirement.GlyphDrain.IsCompleted || !externalPreviewActions.IsEmpty
                    || IsEditorExtensionCommandRunning || IsSpriteSourceWorkDraining || IsAssetThumbnailServiceDraining
                    || sessionHistory?.Extensions?.HasOwnedAssetEditors == true
                    || projectCreationPanel?.IsGenerating == true || addedProjectCreation?.IsGenerating == true) return;
                externalPreviewDrain.GetAwaiter().GetResult();
                componentImageRetirement.GetAwaiter().GetResult();
                retirement.GlyphDrain.GetAwaiter().GetResult();
                ReleaseDrainedScriptGlyphImages();
                retirement.Stage = retiredEditorPreviews.Count != 0 ? GpuRetirementStage.Resources : GpuRetirementStage.Final;
                return; // A fresh submitted boundary must follow owner disposal.
            }
            if (retirement.Stage == GpuRetirementStage.Resources)
            {
                if (!PumpSceneAndResourceRetirementDrains(retirement)) return;
                // Async disposal may enqueue resources after the initial marker.
                // Prove their last submitted use before invoking their disposers.
                if (!externalPreviewActions.IsEmpty) { retirement.Stage = GpuRetirementStage.Owners; return; }
                if (!PollEditorGpuRetirementMarker(retirement, submittedBoundary)) return;
                protectedEditorResourceDisposalDevice = retirement.Device;
                try { ClearRetiredEditorPreviews(); }
                finally { protectedEditorResourceDisposalDevice = null; }
                if (!externalPreviewActions.IsEmpty || retiredEditorPreviews.Count != 0)
                { retirement.Stage = GpuRetirementStage.Owners; return; }
                retirement.Stage = GpuRetirementStage.Final;
                return; // Resource disposal may itself submit/record GPU work.
            }
            if (retirement.Stage == GpuRetirementStage.Final)
            {
                if (projectCreationPanel?.IsGenerating == true || addedProjectCreation?.IsGenerating == true
                    || !externalPreviewActions.IsEmpty || retirement.Marker != null
                    && (retirement.ResourceRevision != retiredEditorPreviewRevision || retirement.ActionRevision != externalPreviewActionRevision))
                {
                    DeferEditorGpuMarker(retirement);
                    retirement.Stage = GpuRetirementStage.Owners;
                    return;
                }
                if (!PollEditorGpuRetirementMarker(retirement, submittedBoundary)) return;
                // A fresh marker follows actual async disposal/actor callbacks;
                // the initial image marker never certifies later cleanup work.
                foreach (var marker in retirement.DeferredMarkers.ToArray())
                { marker.Dispose(); retirement.DeferredMarkers.Remove(marker); }
                CertifyFontResourceGenerations();
                editorGpuRetirementCertifiedDevice = retirement.Device;
                editorGpuRetirementCertifiedResourceRevision = retiredEditorPreviewRevision;
                editorGpuRetirementCertifiedActionRevision = externalPreviewActionRevision;
                editorGpuRetirementCertified = true;
                editorGpuRetirement = null;
                retirement.Completion.TrySetResult();
            }
        }
        catch (Exception error)
        {
            DeferEditorGpuMarker(retirement);
            retirement.RetryAt = Environment.TickCount64 + 250;
            if (retirement.Error != error.ToString())
            { retirement.Error = error.ToString(); RecordAssetPreviewRenderFailure("GPU resource retirement", error); }
            // Failed markers/disposals retain real owners. A later actual marker
            // can prove progress; no timeout turns failure into completion.
        }
    }

    private bool PollEditorGpuRetirementMarker(EditorGpuRetirement retirement, bool submittedBoundary)
    {
        if (retirement.Marker != null && (retirement.ResourceRevision != retiredEditorPreviewRevision
            || retirement.ActionRevision != externalPreviewActionRevision || !externalPreviewActions.IsEmpty))
        { DeferEditorGpuMarker(retirement); return false; }
        if (retirement.Marker == null)
        {
            if (!submittedBoundary) return false;
            // Failed/simulated-reset ticks can leave a healthy recording main
            // list even though normal EndDraw was skipped. Its actual work must
            // be submitted before the queue marker; no lost-device commands.
            if (!retirement.Device.IsPhysicallyUnavailable) GraphicsContext.CommandList.SubmitRecordedWorkForRetirement();
            retirement.Marker = retirement.Device.CreateGpuCompletion()
                ?? throw new NotSupportedException("The selected graphics backend cannot certify GPU resource retirement.");
            retirement.ResourceRevision = retiredEditorPreviewRevision;
            retirement.ActionRevision = externalPreviewActionRevision;
        }
        var marker = retirement.Marker;
        if (!ReferenceEquals(marker.Device, retirement.Device))
            throw new InvalidOperationException("A GPU retirement marker belongs to a different graphics device.");
        switch (marker.Poll())
        {
            case GraphicsGpuCompletionStatus.Pending: return false;
            case GraphicsGpuCompletionStatus.Completed:
            case GraphicsGpuCompletionStatus.DeviceUnavailable when retirement.Device.IsPhysicallyUnavailable:
                marker.Dispose(); retirement.Marker = null; retirement.Error = null; return true;
            default: throw new InvalidOperationException(marker.Failure ?? "GPU completion could not be proved.");
        }
    }
    private static void DeferEditorGpuMarker(EditorGpuRetirement retirement)
    {
        if (retirement.Marker == null) return;
        retirement.DeferredMarkers.Add(retirement.Marker); retirement.Marker = null;
    }
    private void EnsureEditorGpuRetirementCompleted()
    {
        if (editorGpuRetirement != null)
            throw new InvalidOperationException("GPU resource retirement must complete before editor/device/content teardown.");
        if (exitApproved && (!editorGpuRetirementCertified || !ReferenceEquals(editorGpuRetirementCertifiedDevice, GraphicsDevice)
            || editorGpuRetirementCertifiedResourceRevision != retiredEditorPreviewRevision
            || editorGpuRetirementCertifiedActionRevision != externalPreviewActionRevision))
            throw new InvalidOperationException("Terminal teardown requires the actual final GPU certificate for the unchanged resource/action generation.");
    }
}
