using Stride.Assets.Presentation.AssetEditors.GameEditor;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private WeakReference<SceneTransformGizmos>? configuredSceneTransformView;
    private SceneEditorTransientState? configuredSceneTransformState;
    private void RestoreSceneTransformationViewState(bool force = false)
    {
        if (engineViewport?.Gizmos is not { } gizmos) return;
        var state = CurrentSceneEditorTransientState();
        if (!force && configuredSceneTransformView?.TryGetTarget(out var previous) == true
            && ReferenceEquals(previous, gizmos) && ReferenceEquals(state, configuredSceneTransformState)) return;
        gizmos.Mode = state?.Transformation ?? 0;
        gizmos.Space = state?.Space ?? TransformationSpace.WorldSpace;
        configuredSceneTransformView = new(gizmos); configuredSceneTransformState = state;
    }
    private void SaveSceneTransformationViewState()
    {
        if (IsRecoveryWorkspacePresentationRestoring) return;
        if (engineViewport?.Gizmos is not { } gizmos || CurrentSceneEditorTransientState() is not { } state) return;
        state.Transformation = gizmos.Mode; state.Space = gizmos.Space;
    }
}
