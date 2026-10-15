using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Gizmos;
using Stride.Assets.Presentation.AssetEditors.Gizmos;

namespace Stride.Editor.ImGui;

internal sealed record SceneOffsetRuntimeTarget(SceneDocument Owner, Entity Anchor, Scene Scene, Vector3 ExternalOffset);

internal sealed partial class EngineViewport
{
    private readonly Dictionary<SceneDocument, SceneOffsetRuntimeTarget> sceneOffsetAnchors = new(ReferenceEqualityComparer.Instance);
    private readonly List<SceneDocument> staleSceneOffsetAnchors = [];
    internal SceneOffsetRuntimeTarget? ResolveSceneOffsetTarget(SceneDocument owner)
    {
        var runtime = ReferenceEquals(owner, PresentedDocument) ? loadedScene
            : childScenes.TryGetValue(owner.Asset.Id, out var child) && ReferenceEquals(child.Document, owner) ? child.Scene : null;
        if (runtime?.Parent == null || SceneLoaded?.Invoke(owner) == false) return null;
        var external = ReferenceEquals(owner, PresentedDocument) ? (ResolveSceneWorldOffset?.Invoke(owner) ?? owner.Asset.Offset) - owner.Asset.Offset : Vector3.Zero;
        if (!sceneOffsetAnchors.TryGetValue(owner, out var target) || !ReferenceEquals(target.Scene, runtime)
            || !ReferenceEquals(target.Anchor.Scene, runtime.Parent))
        {
            if (target != null) target.Anchor.Scene = null;
            // Exact native SceneEditorGame virtual-anchor policy: real runtime Scene.Id, no authored part identity.
            var anchor = new Entity { Id = runtime.Id, Name = $"Virtual anchor of scene {runtime.Id}" };
            anchor.Tags.Add(GizmoBase.NoGizmoKey, true);
            runtime.Parent.Entities.Add(anchor);
            target = new(owner, anchor, runtime, external);
            sceneOffsetAnchors[owner] = target;
        }
        else if (target.ExternalOffset != external) sceneOffsetAnchors[owner] = target = target with { ExternalOffset = external };
        if (!owner.IsEditingSceneOffset) RestoreSceneOffsetTarget(target);
        return target;
    }
    internal static void RestoreSceneOffsetTarget(SceneOffsetRuntimeTarget target)
    {
        target.Anchor.Transform.Position = target.Owner.Asset.Offset + target.ExternalOffset;
        target.Anchor.Transform.UpdateWorldMatrix();
        target.Scene.Offset = target.Anchor.Transform.Position;
        target.Scene.UpdateWorldMatrix();
    }
    private void PruneSceneOffsetAnchors()
    {
        staleSceneOffsetAnchors.Clear();
        foreach (var pair in sceneOffsetAnchors)
        {
            var runtime = ReferenceEquals(pair.Key, PresentedDocument) ? loadedScene
                : childScenes.TryGetValue(pair.Key.Asset.Id, out var child) && ReferenceEquals(child.Document, pair.Key) ? child.Scene : null;
            if (ReferenceEquals(runtime, pair.Value.Scene) && runtime?.Parent != null && SceneLoaded?.Invoke(pair.Key) != false) continue;
            staleSceneOffsetAnchors.Add(pair.Key);
        }
        if (staleSceneOffsetAnchors.Count > 0) Gizmos?.Cancel();
        foreach (var owner in staleSceneOffsetAnchors) { sceneOffsetAnchors[owner].Anchor.Scene = null; sceneOffsetAnchors.Remove(owner); }
        staleSceneOffsetAnchors.Clear();
    }
    private void ClearSceneAnchors()
    {
        Gizmos?.Cancel();
        foreach (var target in sceneOffsetAnchors.Values) target.Anchor.Scene = null;
        sceneOffsetAnchors.Clear();
    }
}
