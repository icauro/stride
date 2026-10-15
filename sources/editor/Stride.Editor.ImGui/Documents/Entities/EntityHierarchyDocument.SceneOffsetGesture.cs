using Stride.Core.Mathematics;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    private EditorDeferredAssetMutation? sceneOffsetMutation;
    private readonly List<EditorDeferredAssetMutation> sceneOffsetMutationCleanup = [];
    internal bool IsEditingSceneOffset => sceneOffsetMutation != null;
    internal bool HasActiveSceneOffsetChange => sceneOffsetMutation?.HasChanges == true;
    internal void BeginSceneOffsetEdit()
    {
        if (sceneOffsetMutation != null) return;
        EndEdits();
        if (!CanEditSceneOffset(SceneOffsetNode)) throw new InvalidOperationException("This scene offset cannot be transformed.");
        sceneOffsetMutation = BeginExtensionCommandMutation();
    }
    internal void UpdateSceneOffsetGesture(Vector3 value)
    {
        if (sceneOffsetMutation == null || !CanEditSceneOffset(SceneOffsetNode)) throw new InvalidOperationException("The scene offset gesture owner changed.");
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z)) throw new ArgumentException("The scene offset must be finite.");
        if (!Equals(SceneOffsetNode.Retrieve(), value)) SceneOffsetNode.Update(value);
    }
    internal void EndSceneOffsetEdit()
    {
        ReleaseSceneOffsetMutationCleanup();
        if (sceneOffsetMutation is not { } mutation) return;
        if (CompleteMultiTransformGroup is { } complete) { complete(); return; }
        mutation.Commit("Transform scenes");
        sceneOffsetMutation = null;
        try { mutation.Dispose(); } catch { sceneOffsetMutationCleanup.Add(mutation); throw; }
    }
    internal void CancelSceneOffsetEdit()
    {
        if (sceneOffsetMutation is not { } mutation) return;
        mutation.Rollback();
        mutation.Dispose();
        sceneOffsetMutation = null;
    }
    internal void ReleaseSceneOffsetMutationCleanup()
    {
        foreach (var mutation in sceneOffsetMutationCleanup.ToArray())
        { mutation.Dispose(); sceneOffsetMutationCleanup.Remove(mutation); }
    }
}
