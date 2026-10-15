using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets.Quantum.Visitors;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal bool CanChangeObjectProperty(ComponentField field)
    {
        if (!field.SupportsObjectLifecycle || !CanEditHierarchyField(field)
            || !AssetMetadataDocument.CanAssignAuthoringNode(field.Node)) return false;
        EnsureHierarchyGraph();
        return !hierarchyGraph!.Definition.IsMemberTargetObjectReference(field.Node, field.Node.Retrieve())
            && IsKnownComponentField(field);
    }

    internal void ChangeObjectProperty(ComponentField field, Type? type)
    {
        if (!CanChangeObjectProperty(field) || (type == null ? !field.AllowsNullObject : !field.ObjectTypes.Contains(type)))
            throw new InvalidOperationException("This object property cannot be changed.");
        EndEdits(); EnsureHierarchyGraph();
        var before = field.Node.Retrieve();
        if (before?.GetType() == type || before == null && type == null) return;
        object? after = null;
        if (type != null)
        {
            var created = ObjectFactoryRegistry.NewInstance(type);
            if (created == null || !field.DeclaredType.IsInstanceOfType(created)) throw new InvalidOperationException("The object factory returned an incompatible value.");
            var node = hierarchyGraph!.Container.NodeContainer.GetOrCreateNode(created);
            var external = ExternalReferenceCollector.GetExternalReferences(hierarchyGraph.Definition, node);
            after = AssetCloner.Clone(created, AssetClonerFlags.GenerateNewIdsForIdentifiableObjects, external, out _);
            AssetCollectionItemIdHelper.GenerateMissingItemIds(after!);
        }
        var removedIds = before == null || field.Node.Target == null ? new HashSet<Guid>()
            : IdentifiableObjectCollector.Collect(hierarchyGraph!.Definition, field.Node.Target).Keys.ToHashSet();
        if (before is IIdentifiable identifiable) removedIds.Add(identifiable.Id);
        ApplyInheritedGraphMutation(() =>
        {
            field.Node.Update(after);
            if (removedIds.Count != 0) HierarchyGraph.ClearReferencesToObjects(removedIds);
        }, "Edit " + field.Label);
    }
}
