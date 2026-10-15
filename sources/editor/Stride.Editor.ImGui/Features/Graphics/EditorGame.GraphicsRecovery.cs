using System.Collections.Concurrent;
using Hexa.NET.ImGui;
using Stride.Core.Diagnostics;
using Stride.Graphics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private GraphicsRecoveryState? editorGraphicsRecovery;
    private int graphicsRecoveryDetachedBuilds;
    private readonly ConcurrentQueue<Action> graphicsRecoveryCleanup = new();
    private bool IsEditorGraphicsRecovering => editorGraphicsRecovery != null;
    private ImGuiErrorRecoveryState graphicsFrameScope;
    private bool graphicsFrameOpen;

    private sealed class GraphicsRecoveryState
    {
        public required ProjectWorkspace? Workspace;
        public required EditorSessionHistory? History;
        public required EntityHierarchyDocument? PresentedDocument;
        public CompiledProjectContent? SceneBundle;
        public required SceneCameraSettings? Camera;
        public required int Width, Height;
        public required bool PlatformWindows;
        public GraphicsProfile Profile;
        public ColorSpace ColorSpace;
        public required string? PreviewPath;
        public string PreviewKind = "";
        public string? SelectedAssetPath;
        public CompiledProjectContent? PreviewBundle;
        public Action? RestorePreviewView;
        public bool JobsDetached, SceneViewsCaptured;
        public bool PreviewCreated;
        public Task? RecoveryCheckpoint;
        public bool RecoveryCheckpointStarted, RecoveryCheckpointObserved;
        public int ReleasePosition, RebindPosition;
        public string? LastError;
        public CompiledProjectContent? AnimationBundle;
        public bool OwnsAnimationBundle;
        public readonly HashSet<object> Released = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<object, string> RetainedModelUrls = new(ReferenceEqualityComparer.Instance);
    }

    // Hooks called by the host at the actual failed-tick and next BeginDraw boundaries.
    private bool RequestEditorGraphicsRecovery(GraphicsDeviceException error)
    {
        if (guiContext.IsNull || smokeTest) return false;
        if (error.Status == GraphicsDeviceStatus.Normal) return false;
        if (editorGraphicsRecovery != null) return true;
        if (graphicsFrameOpen)
        {
            ImGuiP.ErrorRecoveryTryToRecoverState(ref graphicsFrameScope);
            Gui.EndFrame(); graphicsFrameOpen = false;
        }
        var current = engineViewport;
        var state = new GraphicsRecoveryState
        {
            Workspace = workspace, History = sessionHistory,
            PresentedDocument = current?.PresentedDocument ?? document,
            SceneBundle = current?.SourceContent ?? projectContent,
            Camera = current == null ? null : current.Camera.Capture(showSceneSelection) with { Grid = current.Grid?.Settings, CameraPreview = current.ShowCameraPreview },
            Width = current?.Target?.Width ?? 0, Height = current?.Target?.Height ?? 0,
            PlatformWindows = platformViewports != null, PreviewPath = assetPreviewPath, SelectedAssetPath = selectedAssetPath,
            Profile = GraphicsDevice.Features.RequestedProfile, ColorSpace = GraphicsDevice.ColorSpace,
        };
        state.RestorePreviewView = CaptureGraphicsRecoveryPreview(state);
        graphicsFailureClosePrompted = false;
        editorGraphicsRecovery = state;
        try { CaptureSceneDocumentGraphicsRecovery(state); state.SceneViewsCaptured = true; }
        catch (Exception captureError)
        {
            state.LastError = captureError.ToString();
            outputLog.Append("Graphics", LogMessageType.Error, "Scene recovery capture", captureError.Message, state.LastError);
        }
        SuspendGraphicsDeviceRecovery(ReleaseEditorGraphicsForRecovery, RebuildEditorGraphicsAfterRecovery);
        outputLog.Append("Graphics", LogMessageType.Error, "Device", error.Message, error.ToString());
        return true;
    }

    private bool PumpEditorGraphicsRecovery()
    {
        PumpSpriteSourceCleanup();
        PumpRetiredAssetThumbnailServices();
        PumpAnimationPreviewScrub();
        PumpExternalAssetPreview();
        PumpEditorGpuRetirement(submittedBoundary: true);
        PumpSceneDocumentViewDrain();
        PumpScriptGlyphImages(submittedBoundary: true);
        PumpEditorExtensionCommands();
        PumpRecoveryTransitions();
        if (exitApproved) return false;
        if (editorGraphicsRecovery is not { } state) return true;
        Gui.SetCurrentContext(guiContext);
        try
        {
            if (!ReferenceEquals(workspace, state.Workspace) || !ReferenceEquals(sessionHistory, state.History))
                throw new InvalidOperationException("The authored session changed during graphics recovery.");
            if (!state.SceneViewsCaptured)
            { CaptureSceneDocumentGraphicsRecovery(state); state.SceneViewsCaptured = true; }
            if (!state.JobsDetached)
            {
                DetachGraphicsRecoveryJobs(); state.JobsDetached = true;
                PumpEditorGpuRetirement(submittedBoundary: true);
                PumpSceneDocumentViewDrain();
                PumpScriptGlyphImages(submittedBoundary: true);
            }
            // These callbacks own real staged document transactions, rather than GPU work.
            // Drain their actor queue before taking the final runtime snapshot.
            PumpPackageDependencyRestore(); PumpPackageDependencyRemovalCheck();
            PumpCancelledProjectCreation(); PumpAddedSolutionProjects();
            PumpPackagePlatformUpdate();
            PumpPackageMetadataSave();
            if (IsEditorGpuRetirementPending || IsExternalAssetPreviewDrainPending || IsExternalAssetPreviewUpdating || !componentImageRetirement.IsCompleted) return false;
            if (packageDependencyRestore is { Task.IsCompleted: false } || packageDependencyRestore?.Pending.IsEmpty == false
                || IsEditorExtensionCommandRunning || IsPackageDependencyChecking || IsPackagePlatformUpdating || IsPackageMetadataSaving || Volatile.Read(ref graphicsRecoveryDetachedBuilds) != 0
                || Volatile.Read(ref detachedFontBuilds) != 0 || Volatile.Read(ref detachedCompositorBuilds) != 0
                || Volatile.Read(ref detachedAssetImports) != 0)
                return false;
            while (graphicsRecoveryCleanup.TryDequeue(out var cleanup))
            {
                try { cleanup(); }
                catch { graphicsRecoveryCleanup.Enqueue(cleanup); throw; }
            }
            if (!state.RecoveryCheckpointStarted)
            {
                // Capture actual dirty documents on this actor; only detached recovery bytes
                // are written asynchronously. This also preserves a failed native replacement.
                try { state.RecoveryCheckpoint = ownedRecovery?.FlushTransitionAsync() ?? Task.CompletedTask; }
                catch (Exception error) { state.RecoveryCheckpoint = Task.FromException(error); }
                state.RecoveryCheckpointStarted = true;
            }
            if (state.RecoveryCheckpoint?.IsCompleted != true) return false;
            if (!state.RecoveryCheckpointObserved)
            {
                try { state.RecoveryCheckpoint.GetAwaiter().GetResult(); }
                catch (Exception error) { outputLog.Append("Graphics", LogMessageType.Error, "Recovery checkpoint", error.Message, error.ToString()); }
                state.RecoveryCheckpointObserved = true;
            }
            if (!PumpGraphicsRecoveryClose(state)) return false;
            // Streaming cancellation is polled across frames; no wait or lost-device upload.
            if (!Streaming.TryReleaseGraphicsResourcesForRecovery()) return false;
            RecoverGraphicsDevice(ReleaseEditorGraphicsForRecovery, RebuildEditorGraphicsAfterRecovery);
            if (IsGraphicsDeviceRecoveryPending) return false;
            ClearSceneDocumentGraphicsRecovery(state);
            editorGraphicsRecovery = null;
            statusMessage = "Graphics device recovered";
            outputLog.Append("Graphics", LogMessageType.Info, "Device", statusMessage);
            return true;
        }
        catch (Exception error)
        {
            var text = error.ToString();
            if (state.LastError != text)
            {
                state.LastError = text; statusMessage = error.Message;
                outputLog.Append("Graphics", LogMessageType.Error, "Recovery", error.Message, text);
            }
            if (GraphicsDeviceRecoveryFailure != null) PumpGraphicsRecoveryClose(state);
            return false;
        }
    }

    protected override bool OnGraphicsDeviceLost(GraphicsDeviceException error) => RequestEditorGraphicsRecovery(error);
    protected override bool BeginDraw()
    {
        PumpVerificationDeviceRecoveryBoundary();
        if (!PumpEditorGraphicsRecovery()) return false;
        return base.BeginDraw();
    }

    private void DetachGraphicsRecoveryBuild(Task<string>? task, CancellationTokenSource? cancellation, Action<string> release, IDisposable? lease = null)
    {
        cancellation?.Cancel();
        if (task == null) { cancellation?.Dispose(); lease?.Dispose(); return; }
        Interlocked.Increment(ref graphicsRecoveryDetachedBuilds);
        _ = task.ContinueWith(completed =>
        {
            try
            {
                if (completed.Status == TaskStatus.RanToCompletion)
                    graphicsRecoveryCleanup.Enqueue(() => release(completed.Result));
                else if (completed.IsFaulted) _ = completed.Exception;
            }
            finally { lease?.Dispose(); cancellation?.Dispose(); Interlocked.Decrement(ref graphicsRecoveryDetachedBuilds); }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }
}
