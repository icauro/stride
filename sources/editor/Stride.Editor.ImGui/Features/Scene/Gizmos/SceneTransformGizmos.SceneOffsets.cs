using Stride.Engine;

namespace Stride.Editor.ImGui;

internal sealed partial class SceneTransformGizmos
{
    internal Func<IReadOnlyList<SceneDocument>>? SelectionSceneRoots { get; set; }
    internal Func<SceneDocument?>? SelectionSceneRootPrimary { get; set; }
    internal Func<long>? SelectionContentVersion { get; set; }
    internal Func<SceneDocument, SceneOffsetRuntimeTarget?>? ResolveSceneOffsetTarget { get; set; }
    internal Action? BeforeGesture { get; set; }
    private readonly List<EntityHierarchyDocument> candidateOwners = [];
    private long selectionContentVersion = -1;
    private readonly Dictionary<Entity, TransformState> sceneAnchorStarts = new(ReferenceEqualityComparer.Instance);
    private IReadOnlyList<EntityHierarchyDocument> CollectTargetOwners(IReadOnlyList<EntityHierarchyDocument> entityOwners)
    {
        candidateOwners.Clear(); candidateOwners.AddRange(entityOwners);
        foreach (var root in SelectionSceneRoots?.Invoke() ?? []) if (!candidateOwners.Contains(root)) candidateOwners.Add(root);
        return candidateOwners;
    }
    private bool TargetRuntimeChanged(TransformTarget target)
    {
        if (target.SceneRoot is { } root)
            return ResolveSceneOffsetTarget?.Invoke(root.Owner) is not { } current
                || !ReferenceEquals(current.Anchor, root.Anchor) || current.ExternalOffset != root.ExternalOffset;
        return ResolveRuntimeEntity != null && !ReferenceEquals(ResolveRuntimeEntity(target.Owner, target.Authored!.Id), target.Runtime);
    }
    private void BeginGestureTargets()
    {
        gestureOwners = transformTargets.Select(target => target.Owner).Distinct().ToArray();
        foreach (var owner in gestureOwners)
            if (transformOwnerRoots.TryGetValue(owner, out var roots)) owner.BeginMultiTransformEdit(roots);
            else owner.BeginSceneOffsetEdit();
        foreach (var owner in gestureOwners) owner.CompleteMultiTransformGroup = CompleteGroup;
        sceneAnchorStarts.Clear();
        foreach (var target in transformTargets)
            if (target.SceneRoot != null) sceneAnchorStarts[target.Runtime] = TransformState.Read(target.Runtime);
    }
    private void ApplyTargetRuntime(TransformTarget target)
    {
        if (target.SceneRoot is { } root)
        {
            root.Owner.UpdateSceneOffsetGesture(root.Anchor.Transform.Position - root.ExternalOffset);
            root.Scene.Offset = root.Anchor.Transform.Position;
            root.Scene.UpdateWorldMatrix();
        }
        else TransformState.Read(target.Runtime).Apply(target.Authored!);
    }
    private void RestoreTargetRuntime(TransformTarget target)
    {
        if (target.SceneRoot is { } root)
        {
            var current = ResolveSceneOffsetTarget?.Invoke(root.Owner) ?? root;
            EngineViewport.RestoreSceneOffsetTarget(current);
            if (sceneAnchorStarts.TryGetValue(current.Anchor, out var start))
            { current.Anchor.Transform.Rotation = start.Rotation; current.Anchor.Transform.Scale = start.Scale; current.Anchor.Transform.UpdateWorldMatrix(); }
        }
        else TransformState.Read(target.Authored!).Apply(target.Runtime);
    }
    private void FinishGestureTargets()
    {
        foreach (var owner in gestureOwners) { owner.EndMultiTransformEdit(); owner.EndSceneOffsetEdit(); }
    }
}
