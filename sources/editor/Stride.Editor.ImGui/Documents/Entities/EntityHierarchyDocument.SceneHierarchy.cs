using Stride.Assets.Entities;
using Stride.Core.Assets;
using Stride.Core.Mathematics;
using Stride.Core.Quantum;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal void ApplySceneHierarchy(AssetId[] children, Scene? parent, Vector3 offset, params EditorAuthoringState[] participants)
    {
        if (Asset is not SceneAsset scene || !CanSave) throw new InvalidOperationException("This scene hierarchy cannot be edited.");
        if (children.Distinct().Count() != children.Length || !float.IsFinite(offset.X) || !float.IsFinite(offset.Y) || !float.IsFinite(offset.Z))
            throw new ArgumentException("The scene hierarchy state is invalid.");
        EndEdits(); EnsureHierarchyGraph();
        var previousChildren = scene.ChildrenIds.ToArray(); var previousParent = scene.Parent; var previousOffset = scene.Offset;
        var beforeRevision = revision; var afterRevision = ++nextRevision;
        var node = hierarchyGraph!.RootNode;
        var childrenNode = node[nameof(SceneAsset.ChildrenIds)].Target!;
        void Apply(AssetId[] ids, Scene? sceneParent, Vector3 sceneOffset, long next)
        {
            for (var index = scene.ChildrenIds.Count - 1; index >= 0; index--) childrenNode.Remove(scene.ChildrenIds[index], new NodeIndex(index));
            for (var index = 0; index < ids.Length; index++) childrenNode.Add(ids[index], new NodeIndex(index));
            node[nameof(SceneAsset.Parent)].Update(sceneParent); node[nameof(SceneAsset.Offset)].Update(sceneOffset);
            revision = next; StructureVersion++;
        }
        void Replay(AssetId[] ids, Scene? sceneParent, Vector3 sceneOffset, long next)
        {
            var currentChildren = scene.ChildrenIds.ToArray(); var currentParent = scene.Parent; var currentOffset = scene.Offset; var currentRevision = revision;
            try { Apply(ids, sceneParent, sceneOffset, next); }
            catch (Exception failure)
            {
                try { Apply(currentChildren, currentParent, currentOffset, currentRevision); }
                catch (Exception recovery) { throw new AggregateException("Scene hierarchy mutation and restoration failed.", failure, recovery); }
                throw;
            }
        }
        void Restore() => Replay(previousChildren, previousParent, previousOffset, beforeRevision);
        void Change() => Replay(children, parent, offset, afterRevision);
        var changed = false; var recorded = false;
        try
        {
            using var transaction = CreateDocumentTransaction("Move scene");
            Change(); changed = true; RecordOperation(new HierarchyCreationOperation(Restore, Change), participants); recorded = true;
        }
        catch { if (changed && !recorded) Restore(); throw; }
    }
}
