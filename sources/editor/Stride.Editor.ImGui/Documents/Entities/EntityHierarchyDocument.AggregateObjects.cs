using Stride.Assets.Models;
using Stride.Assets.Presentation.NodePresenters.Rules;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets.Quantum.Visitors;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Rendering;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    private static bool SameAggregateOwnedValue(object? current, object? expected)
        => expected != null && expected.GetType().IsValueType ? Equals(current, expected) : ReferenceEquals(current, expected);

    internal Action PrepareAggregateCollectionUpdate(CollectionItem item, object? value)
    {
        ValidateAggregateCollectionItem(item);
        if (value == null ? !item.AllowsNull : !item.Type.IsInstanceOfType(value)) throw new ArgumentException("The item value is incompatible.");
        if (item.IsContentReference) ContentAssetReference.Validate(item.Type, value, item.AllowsNull);
        if (item.IsSceneReference && value != null)
        {
            var target = value is Stride.Engine.Entity entity ? entity : (value as Stride.Engine.EntityComponent)?.Entity;
            if (!SceneObjectReference.Accepts(item.Type, value) || target == null
                || !Asset.Hierarchy.Parts.TryGetValue(target.Id, out var part) || !ReferenceEquals(part.Entity, target)
                || value is Stride.Engine.EntityComponent component && !target.Components.Contains(component))
                throw new ArgumentException("The reference must belong to each selected property's document.");
        }
        AssertHierarchyItemAxesEditable(item, value);
        var normalized = MatrixPropertyValue.Normalize(PropertyValueValidation.Normalize(value, HierarchyItemNumericRange(item)), item.Value);
        normalized = NativeNumericAxes.Normalize(normalized, item.Value, item.AxisNumericRanges);
        return () => { ValidateAggregateCollectionItem(item); if (item.IsContentReference ? !ContentAssetReference.AreEqual(item.Value, normalized) : !Equals(item.Value, normalized)) item.Collection.Update(normalized, item.Index); };
    }
    internal Action PrepareAggregateCollectionReferenceAdd(ComponentField field, object value, string? dictionaryKey = null)
    {
        if (!CanEditAggregateCollection(field) || !CanResizeAggregateCollection(field) || field.IsDictionary && !CanAddAggregateDictionaryKeys(field))
            throw new InvalidOperationException("The selected collection cannot be resized.");
        var declared = field.CollectionElementType!;
        if (ContentAssetReference.Supports(declared)) ContentAssetReference.Validate(declared, value, false);
        else
        {
            var target = value is Stride.Engine.Entity entity ? entity : (value as Stride.Engine.EntityComponent)?.Entity;
            if (!SceneObjectReference.Accepts(declared, value) || target == null
                || !Asset.Hierarchy.Parts.TryGetValue(target.Id, out var part) || !ReferenceEquals(part.Entity, target)
                || value is Stride.Engine.EntityComponent component && !target.Components.Contains(component))
                throw new ArgumentException("The reference must belong to each selected property's document.");
        }
        var key = field.IsDictionary ? CreateDictionaryAddKey(field, dictionaryKey) : null;
        if (key != null) EnsureUniqueDictionaryKey(field, key);
        return () =>
        {
            if (!CanEditAggregateCollection(field) || !CanResizeAggregateCollection(field)) throw new InvalidOperationException("The selected collection changed.");
            if (key != null) EnsureUniqueDictionaryKey(field, key);
            var collection = (IAssetObjectNode)field.Node.Target!;
            collection.Add(value, key == null ? new NodeIndex((collection.Indices ?? []).Count()) : new NodeIndex(key));
        };
    }
    internal bool CanEditAggregateCollectionObject(CollectionItem item)
        => CanEditAggregateCollection(item.Field) && ReferenceEquals(item.Field.Node.Target, item.Collection)
        && item.Collection.HasId(item.Id) && CanEditHierarchyItem(item.Collection, item.Index) && !item.SupportsSimpleEditing
        && !HierarchyGraph.Definition.IsTargetItemObjectReference(item.Collection, item.Index, item.Value);
    internal Action PrepareAggregateCollectionObjectChange(CollectionItem item, Type? type)
    {
        ValidateAggregateCollectionItem(item);
        if (!CanEditAggregateCollectionObject(item) || (type == null ? !item.AllowsNull : !CollectionItemTypes(item.Field).Contains(type)))
            throw new InvalidOperationException("The selected collection object cannot be changed.");
        var before = item.Value;
        if (before?.GetType() == type || before == null && type == null) return () => { };
        var after = CreateCollectionValue(item.Field, type);
        return () =>
        {
            ValidateAggregateCollectionItem(item);
            if (!CanEditAggregateCollectionObject(item) || !SameAggregateOwnedValue(item.Value, before))
                throw new InvalidOperationException("The selected collection object changed.");
            var removed = before == null || item.Collection.ItemReferences == null || item.Collection.IndexedTarget(item.Index) is not { } target ? new HashSet<Guid>()
                : IdentifiableObjectCollector.Collect(HierarchyGraph.Definition, target).Keys.ToHashSet();
            if (before is IIdentifiable identifiable) removed.Add(identifiable.Id);
            item.Collection.Update(after, item.Index);
            if (removed.Count != 0) HierarchyGraph.ClearReferencesToObjects(removed);
        };
    }
    internal bool CanEditAggregateObject(ComponentField field)
        => SessionHistory != null && ReferenceEquals(SessionHistory.Registry.Find(Asset.Id)?.Document, this)
        && field.Component.Entity is { } entity && IsSelected(entity.Id) && CanEditHierarchyField(field)
        && field.SupportsObjectLifecycle && field.Node is IAssetMemberNode { CanOverride: true }
        && !HierarchyGraph.Definition.IsMemberTargetObjectReference(field.Node, field.Node.Retrieve());
    internal Action PrepareAggregateObjectChange(ComponentField field, Type? type)
    {
        if (!CanEditAggregateObject(field) || (type == null ? !field.AllowsNullObject : !field.ObjectTypes.Contains(type)))
            throw new InvalidOperationException("The selected object cannot be changed.");
        var before = field.Node.Retrieve();
        if (before?.GetType() == type || before == null && type == null) return () => { };
        object? after = null;
        if (type != null)
        {
            var created = ObjectFactoryRegistry.NewInstance(type);
            if (created == null || !field.DeclaredType.IsInstanceOfType(created)) throw new InvalidOperationException("The object factory returned an incompatible value.");
            var node = HierarchyGraph.Container.NodeContainer.GetOrCreateNode(created);
            after = AssetCloner.Clone(created, AssetClonerFlags.GenerateNewIdsForIdentifiableObjects,
                ExternalReferenceCollector.GetExternalReferences(HierarchyGraph.Definition, node), out _);
            AssetCollectionItemIdHelper.GenerateMissingItemIds(after!);
        }
        return () =>
        {
            if (!CanEditAggregateObject(field) || !SameAggregateOwnedValue(field.Node.Retrieve(), before))
                throw new InvalidOperationException("The selected object changed.");
            var removed = before == null || field.Node.Target == null ? new HashSet<Guid>()
                : IdentifiableObjectCollector.Collect(HierarchyGraph.Definition, field.Node.Target).Keys.ToHashSet();
            if (before is IIdentifiable identifiable) removed.Add(identifiable.Id);
            field.Node.Update(after);
            if (removed.Count != 0) HierarchyGraph.ClearReferencesToObjects(removed);
        };
    }
    internal bool CanEditAggregateMaterialSlot(ComponentField field, int index)
        => CanEditAggregateCollection(field) && CanEditModelMaterialSlot(field, index);
    internal Action PrepareAggregateMaterialChange(ComponentField field, int index, IModelAsset model, Material? value, bool? enabled)
    {
        if (!CanEditAggregateMaterialSlot(field, index) || index < 0 || index >= model.Materials.Count)
            throw new InvalidOperationException("The selected material slot cannot be edited.");
        if (value != null) ContentAssetReference.Validate(typeof(Material), value, true);
        return () =>
        {
            if (!CanEditAggregateMaterialSlot(field, index)) throw new InvalidOperationException("The selected material slot changed.");
            var node = ModelMaterialsNode((Stride.Engine.ModelComponent)field.Component); var key = new NodeIndex(index);
            if (HierarchyGraph.IsInheritedNodeSealed(node, key)) throw new InvalidOperationException("The selected material slot is sealed.");
            if (enabled.HasValue)
            {
                if (enabled.Value != ModelMaterialNodeRules.IsEnabled(node, key)) ModelMaterialNodeRules.SetEnabled(node, key, enabled.Value, model);
            }
            else ModelMaterialNodeRules.Write(node, key, value!);
        };
    }
}
