using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    // The failed-state guard reads this only while handling/rechecking a failure.
    // Include live gesture values, because transaction revisions change on commit.
    internal SceneSynchronizationState SynchronizationState => new(revision,
        activeComponentField, activeComponentField?.Node.Retrieve(), editingEntity,
        editingEntity == null ? default : TransformState.Read(editingEntity), MultiTransformSynchronizationVersion,
        commonPropertySynchronizationVersion, collectionItemSynchronizationVersion,
        Asset is Stride.Assets.Entities.SceneAsset scene ? scene.Offset : default);
}

internal readonly record struct SceneSynchronizationState(long Revision, ComponentField? Field,
    object? Value, Entity? Entity, TransformState Transform, long MultiTransformVersion,
    long CommonPropertyVersion, long CollectionItemVersion, Stride.Core.Mathematics.Vector3 SceneOffset);
