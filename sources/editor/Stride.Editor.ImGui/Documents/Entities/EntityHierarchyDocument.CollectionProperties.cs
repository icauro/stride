using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets.Quantum.Visitors;
using Stride.Core.Extensions;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    private readonly Dictionary<(ComponentField, ItemId), IReadOnlyList<ComponentField>> collectionItemFields = [];
    private readonly Dictionary<ComponentField, IReadOnlyList<CollectionItem>> collectionItems = [];
    private readonly Dictionary<ComponentField, IReadOnlyList<Type>> collectionTypes = [];
    internal bool IsKnownComponentField(ComponentField field)
    {
        RefreshNestedHierarchyCollectionCache();
        return Flatten(GetComponentFields(field.Component)).Contains(field)
            || collectionItemFields.Values.Any(fields => Flatten(fields).Contains(field))
            || nestedHierarchyItemFields.Values.Any(fields => Flatten(fields).Contains(field));
    }

    internal IReadOnlyList<ComponentField> GetCollectionItemFields(CollectionItem item)
    {
        if (item.Value == null || item.SupportsSimpleEditing || item.Collection.ItemReferences == null || item.Collection.IndexedTarget(item.Index) is not { } target
            || hierarchyGraph!.Definition.IsTargetItemObjectReference(item.Collection, item.Index, item.Value)) return [];
        var key = (item.Field, item.Id);
        if (collectionItemFields.TryGetValue(key, out var cached)) return cached;
        ComponentField Map(ComponentField field)
        {
            var path = item.RuntimePath();
            foreach (var member in field.Path) path.Push(member);
            return field with { RuntimePath = path, Name = item.Field.Name + "/" + item.Id + "/" + field.Name,
                Children = field.Children.Select(Map).ToArray() };
        }
        var fields = AttachPropertyPresentation(target, BuildFields(item.Field.Component, target, [], new HashSet<IObjectNode>(), 0), item.DeclaredType).Select(Map).ToArray();
        collectionItemFields.Add(key, fields); AddSynchronizationFields(fields); return fields;
    }
    private CollectionItem? activeCollectionItem;
    private object? collectionEditStart;
    private EditorQuantumMutation? collectionEditMutation;
    private long collectionItemSynchronizationVersion;
    internal bool HasActiveCollectionChange => activeCollectionItem != null && !Equals(collectionEditStart, activeCollectionItem.Value);

    internal bool CanEditCollection(ComponentField field) => (field.IsIndexedCollection || field.IsDictionary) && field.Node.Target is IAssetObjectNode
        && CanEditHierarchyField(field) && CanEditHierarchyNode(field.Node.Target)
        && (field.CollectionDescriptor is not DictionaryDescriptor dictionary || field.Node.Retrieve() is { } value && !dictionary.IsReadOnly(value))
        && IsKnownComponentField(field);

    internal IReadOnlyList<CollectionItem> GetCollectionItems(ComponentField field)
    {
        if (collectionItems.TryGetValue(field, out var cached)) return cached;
        var items = field.Node.Target is IAssetObjectNode collection
            ? (collection.Indices ?? []).Select(index => new CollectionItem(field, collection, collection.IndexToId(index))).ToArray() : [];
        collectionItems.Add(field, items); return items;
    }

    internal bool CanAddCollection(ComponentField field) => CanEditCollection(field) && field.CollectionRules?.ReadOnly != true
        && NativePropertyClipboard.HasAdd((IAssetObjectNode)field.Node.Target!)
        && (field.IsDictionary || Stride.Core.Assets.AssetCollectionCommandRules.CanAdd(field.CollectionElementType!));
    internal bool CanRemoveCollection(ComponentField field) => CanEditCollection(field) && field.CollectionRules?.ReadOnly != true
        && NativePropertyClipboard.HasRemove((IAssetObjectNode)field.Node.Target!)
        && (field.IsDictionary || Stride.Core.Assets.AssetCollectionCommandRules.CanAdd(field.CollectionElementType!));
    internal bool CanResizeCollection(ComponentField field) => CanAddCollection(field) && CanRemoveCollection(field);

    internal bool CanReorderCollection(ComponentField field) => CanResizeCollection(field) && field.CollectionRules?.CanReorderItems == true
        && field.CollectionDescriptor is CollectionDescriptor { HasInsert: true };

    internal IReadOnlyList<Type> CollectionItemTypes(ComponentField field)
    {
        if (collectionTypes.TryGetValue(field, out var cached)) return cached;
        var types = field.CollectionElementType is { } declared && !SceneObjectReference.Supports(declared)
        ? (Nullable.GetUnderlyingType(declared) ?? declared).GetInheritedInstantiableTypes().Append(Nullable.GetUnderlyingType(declared) ?? declared).Distinct()
            .Where(type => !type.IsAbstract && !type.IsInterface && Attribute.GetCustomAttribute(type, typeof(NonInstantiableAttribute)) == null
                && !typeof(Stride.Engine.Entity).IsAssignableFrom(type) && !typeof(Stride.Engine.EntityComponent).IsAssignableFrom(type)
                && !ContentAssetReference.Supports(type) && (type.IsValueType || ObjectFactoryRegistry.CanCreateInstance(type)))
            .OrderBy(type => DisplayAttribute.GetDisplayName(type) ?? type.Name).ToArray() : [];
        collectionTypes.Add(field, types); return types;
    }

    internal void AddCollectionItem(ComponentField field, Type? type)
    {
        if (field.IsDictionary || !CanAddCollection(field)) throw new InvalidOperationException("This collection cannot be changed.");
        EndEdits();
        InsertCollectionValue(field, CreateCollectionValue(field, type));
    }

    private object? CreateCollectionValue(ComponentField field, Type? type)
    {
        object? value = null;
        var declared = field.CollectionElementType!;
        if (type != null)
        {
            if (!CollectionItemTypes(field).Contains(type)) throw new ArgumentException("The item type is not compatible.");
            value = type.IsValueType ? Activator.CreateInstance(type) : ObjectFactoryRegistry.NewInstance(type);
            if (value != null && !type.IsValueType && value is not string)
            {
                var node = hierarchyGraph!.Container.NodeContainer.GetOrCreateNode(value);
                value = AssetCloner.Clone(value, AssetClonerFlags.GenerateNewIdsForIdentifiableObjects,
                    ExternalReferenceCollector.GetExternalReferences(hierarchyGraph.Definition, node), out _);
                AssetCollectionItemIdHelper.GenerateMissingItemIds(value!);
            }
        }
        if (value == null && (declared.IsValueType && Nullable.GetUnderlyingType(declared) == null || field.CollectionRules?.NotNullItems == true))
            throw new ArgumentException("This collection does not allow null items.");
        return value;
    }

    internal void AddCollectionReference(ComponentField field, object value)
    {
        if (field.IsDictionary || !CanAddCollection(field) || !SceneObjectReference.Accepts(field.CollectionElementType!, value)) throw new ArgumentException("The reference is not compatible.");
        var entity = value is Stride.Engine.Entity target ? target : (value as Stride.Engine.EntityComponent)?.Entity;
        if (entity == null || !Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var part) || !ReferenceEquals(part.Entity, entity)
            || value is Stride.Engine.EntityComponent component && !entity.Components.Contains(component)) throw new ArgumentException("The reference must belong to this scene.");
        EndEdits(); InsertCollectionValue(field, value);
    }

    internal void AddCollectionAssetReference(ComponentField field, object value)
    {
        if (field.IsDictionary || !CanAddCollection(field) || !ContentAssetReference.Supports(field.CollectionElementType!))
            throw new InvalidOperationException("This collection cannot be changed.");
        ContentAssetReference.Validate(field.CollectionElementType!, value, allowsNull: false);
        EndEdits(); InsertCollectionValue(field, value);
    }

    private void InsertCollectionValue(ComponentField field, object? value, NodeIndex? dictionaryIndex = null)
    {
        var collection = (IAssetObjectNode)field.Node.Target!;
        var index = dictionaryIndex ?? (collection.Descriptor is SetDescriptor ? new NodeIndex(value) : new NodeIndex((collection.Indices ?? []).Count()));
        ApplyInheritedGraphMutation(() => collection.Add(value!, index), "Add item: " + field.Label);
    }

    internal void RemoveCollectionItem(CollectionItem item)
    {
        if (!CanRemoveCollection(item.Field) || !CanEditHierarchyItem(item.Collection, item.Index))
            throw new InvalidOperationException("This item cannot be removed.");
        EndEdits();
        var value = item.Value; var index = item.Index; var removedIds = new HashSet<Guid>();
        if (value != null && !HierarchyGraph.Definition.IsTargetItemObjectReference(item.Collection, index, value))
        {
            if (item.Collection.ItemReferences != null && item.Collection.IndexedTarget(index) is { } target)
                removedIds.UnionWith(IdentifiableObjectCollector.Collect(HierarchyGraph.Definition, target).Keys);
            if (value is IIdentifiable identifiable) removedIds.Add(identifiable.Id);
        }
        ApplyInheritedGraphMutation(() =>
        {
            if (removedIds.Count != 0) HierarchyGraph.ClearReferencesToObjects(removedIds);
            item.Collection.Remove(value!, item.Collection.IdToIndex(item.Id));
        }, "Remove item: " + item.Field.Label);
    }

    internal void MoveCollectionItem(CollectionItem item, int target)
    {
        if (!CanReorderCollection(item.Field) || !CanEditHierarchyItem(item.Collection, item.Index))
            throw new InvalidOperationException("This collection cannot be reordered.");
        EndEdits();
        if (target < 0 || target >= (item.Collection.Indices ?? []).Count()) throw new ArgumentOutOfRangeException(nameof(target));
        if (item.Index.Int == target) return;
        var value = item.Value;
        var itemOverride = item.Collection.GetItemOverride(item.Index);
        var keyOverride = item.Collection.GetKeyOverride(item.Index);
        ApplyInheritedGraphMutation(() =>
        {
            var destination = new NodeIndex(target);
            if (item.Collection.BaseNode is IAssetObjectNode source && source.HasId(item.Id))
            {
                item.Collection.Remove(value!, item.Collection.IdToIndex(item.Id));
                item.Collection.Add(value!, destination);
                item.Collection.RestoreItemOverrides(destination, itemOverride | OverrideType.New, keyOverride);
            }
            else
            {
                item.Collection.RemoveAndDiscard(value!, item.Collection.IdToIndex(item.Id), item.Id);
                item.Collection.Restore(value!, destination, item.Id);
                item.Collection.RestoreItemOverrides(destination, itemOverride, keyOverride);
            }
        }, "Move item: " + item.Field.Label);
    }

    internal void UpdateCollectionItem(CollectionItem item, object? value)
    {
        if (item.IsContentReference) ContentAssetReference.Validate(item.Type, value, item.AllowsNull);
        if (!CanEditCollection(item.Field) || !CanEditHierarchyItem(item.Collection, item.Index)) throw new InvalidOperationException("This item cannot be edited.");
        if (value == null ? !item.AllowsNull : !item.Type.IsInstanceOfType(value)) throw new ArgumentException("The item value is not compatible.");
        if (item.IsSceneReference && !SceneObjectReference.Accepts(item.Type, value)) throw new ArgumentException("The reference is not compatible.");
        if (item.IsSceneReference && value != null)
        {
            var entity = value is Stride.Engine.Entity target ? target : (value as Stride.Engine.EntityComponent)?.Entity;
            if (entity == null || !Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var part) || !ReferenceEquals(part.Entity, entity)
                || value is Stride.Engine.EntityComponent component && !entity.Components.Contains(component))
                throw new ArgumentException("The reference must belong to this scene.");
        }
        AssertHierarchyItemAxesEditable(item, value);
        value = MatrixPropertyValue.Normalize(PropertyValueValidation.Normalize(value, HierarchyItemNumericRange(item)), item.Value);
        value = NativeNumericAxes.Normalize(value, item.Value, item.AxisNumericRanges);
        if (item.Collection.Descriptor is SetDescriptor set)
        {
            EndEdits();
            if (set.Contains(item.Collection.Retrieve()!, value)) return;
            var index = item.Index;
            ApplyInheritedGraphMutation(() => item.Collection.Update(value, index), "Edit item: " + item.Field.Label);
            return;
        }
        if (activeCollectionItem != item)
        {
            EndEdits(); activeCollectionItem = item; collectionEditStart = item.Value;
            var beforeRevision = revision; var afterRevision = ++nextRevision;
            collectionEditMutation = new EditorQuantumMutation(HierarchyGraph,
                () => CollectionChanged(item.Field, beforeRevision), () => CollectionChanged(item.Field, afterRevision));
        }
        var previousValue = item.Value;
        try { item.Collection.Update(value, item.Index); }
        catch (Exception failure)
        {
            var mutation = collectionEditMutation!;
            activeCollectionItem = null; collectionEditStart = null; collectionEditMutation = null;
            try
            {
                if (SessionHistory == null) mutation.RestoreInitial();
                else SessionHistory.CompensateInitialMutation(mutation.RestoreInitial);
            }
            catch (Exception recovery)
            {
                SessionHistory?.RetainFailedInitialOperation(mutation, historyScope.AuthoringState!);
                throw new AggregateException("Collection item edit and restoration failed.", failure, recovery);
            }
            finally { mutation.Dispose(); }
            throw;
        }
        finally
        {
            if (!Equals(previousValue, item.Value))
            {
                collectionItemSynchronizationVersion++;
                componentReplacementVersions[item.Field.Component.Id] = ComponentReplacementVersion(item.Field.Component.Id) + 1;
            }
        }
    }

    internal void EndCollectionEdit()
    {
        if (activeCollectionItem == null) return;
        var item = activeCollectionItem; var mutation = collectionEditMutation!;
        activeCollectionItem = null; collectionEditStart = null; collectionEditMutation = null;
        var published = false;
        var title = "Edit item: " + item.Field.Label;
        try
        {
            using var transaction = CreateDocumentTransaction(title);
            mutation.Complete();
            if (mutation.HasEffect) RecordOperation(mutation);
            published = true;
        }
        catch (Exception failure)
        {
            if (!published)
            {
                try
                {
                    if (SessionHistory == null) mutation.RestoreInitial();
                    else SessionHistory.CompensateInitialMutation(mutation.RestoreInitial);
                }
                catch (Exception recovery)
                {
                    SessionHistory?.RetainFailedInitialOperation(mutation, historyScope.AuthoringState!);
                    throw new AggregateException("Collection item commit and restoration failed.", failure, recovery);
                }
            }
            throw;
        }
        finally { mutation.Dispose(); }
        LastAction = title;
    }

    private void CollectionChanged(ComponentField field, long currentRevision)
    {
        revision = currentRevision;
        componentReplacementVersions[field.Component.Id] = ComponentReplacementVersion(field.Component.Id) + 1;
        InvalidateComponentStructure();
    }
}
