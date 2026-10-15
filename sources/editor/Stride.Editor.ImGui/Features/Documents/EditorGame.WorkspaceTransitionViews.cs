namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly HashSet<GraphicsRecoveryState> workspaceTransitionViews = [];
    private readonly HashSet<GraphicsRecoveryState> releasedWorkspaceTransitionViews = [];

    private GraphicsRecoveryState CaptureWorkspaceTransitionViews(ProjectWorkspace owner)
    {
        var viewport = engineViewport;
        var state = new GraphicsRecoveryState
        {
            Workspace = owner, History = sessionHistory,
            PresentedDocument = viewport?.PresentedDocument ?? document,
            SceneBundle = viewport?.SourceContent ?? projectContent,
            Camera = viewport == null ? null : viewport.Camera.Capture(showSceneSelection) with
            { Grid = viewport.Grid?.Settings, CameraPreview = viewport.ShowCameraPreview },
            Width = viewport?.Target?.Width ?? 0, Height = viewport?.Target?.Height ?? 0,
            PlatformWindows = platformViewports != null, PreviewPath = assetPreviewPath, SelectedAssetPath = selectedAssetPath,
            Profile = GraphicsDevice.Features.RequestedProfile, ColorSpace = GraphicsDevice.ColorSpace,
        };
        // Publish the snapshot owner before any fallible lease acquisition.
        workspaceTransitionViews.Add(state);
        CaptureSceneDocumentGraphicsRecovery(state);
        state.RestorePreviewView = CaptureGraphicsRecoveryPreview(state);
        if (state.OwnsAnimationBundle && state.AnimationBundle is { } animation)
        {
            if (!retainedActiveProjectContent.Contains(animation)) retainedActiveProjectContent.Add(animation);
            state.OwnsAnimationBundle = false;
        }
        return state;
    }

    private void ReleaseWorkspaceTransitionNativeViews(GraphicsRecoveryState? state)
    {
        EnsureSceneCpuRetirementCompleted(); EnsureEditorGpuRetirementCompleted();
        if (state != null && releasedWorkspaceTransitionViews.Contains(state)) return;
        if (state != null) ReleaseSceneDocumentGraphicsForRecovery(state);
        else ClearSceneDocumentViews();
        // A legacy view may have no document registry owner. The same completed
        // all-scene barrier covers its worker and submitted graphics work.
        if (engineViewport is { } legacy) { RetireEditorPreview(legacy); engineViewport = null; }
        if (state != null) releasedWorkspaceTransitionViews.Add(state);
    }

    private void ClearWorkspaceTransitionViews()
    {
        foreach (var state in workspaceTransitionViews.ToArray())
            ForgetWorkspaceTransitionViews(state);
    }

    private void ForgetWorkspaceTransitionViews(GraphicsRecoveryState? state)
    {
        if (state == null) return;
        ClearSceneDocumentGraphicsRecovery(state);
        workspaceTransitionViews.Remove(state);
        releasedWorkspaceTransitionViews.Remove(state);
    }
}
