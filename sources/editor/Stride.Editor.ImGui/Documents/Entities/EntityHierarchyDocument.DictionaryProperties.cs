using System.ComponentModel;
using System.Globalization;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal bool CanEditDictionaryKeys(ComponentField field) => CanResizeCollection(field)
        && field.CollectionDescriptor is DictionaryDescriptor descriptor
        && (descriptor.KeyType == typeof(string) || TypeDescriptor.GetConverter(descriptor.KeyType).CanConvertFrom(typeof(string)));

    internal bool CanAddDictionaryKeys(ComponentField field) => CanAddCollection(field)
        && field.CollectionDescriptor is DictionaryDescriptor descriptor && NativeDictionaryKey.CanAdd(descriptor.KeyType);
    internal object CreateDictionaryAddKey(ComponentField field, string? text)
        => field.CollectionDescriptor is DictionaryDescriptor descriptor ? NativeDictionaryKey.CreateForAdd(descriptor.KeyType, text)
            : throw new ArgumentException("This property is not a dictionary.");
    internal object ParseDictionaryKey(ComponentField field, string text)
    {
        if (field.CollectionDescriptor is not DictionaryDescriptor descriptor) throw new ArgumentException("This property is not a dictionary.");
        return NativeDictionaryKey.Parse(descriptor.KeyType, text);
    }

    internal void AddDictionaryItem(ComponentField field, string keyText, Type? valueType)
    {
        if (!CanAddDictionaryKeys(field)) throw new InvalidOperationException("This dictionary cannot be changed.");
        var key = CreateDictionaryAddKey(field, keyText);
        EnsureUniqueDictionaryKey(field, key);
        EndEdits();
        InsertCollectionValue(field, CreateCollectionValue(field, valueType), new NodeIndex(key));
    }

    internal void AddDictionaryReference(ComponentField field, string keyText, object value)
    {
        if (!CanAddDictionaryKeys(field) || !SceneObjectReference.Accepts(field.CollectionElementType!, value)) throw new ArgumentException("The reference is not compatible.");
        var key = CreateDictionaryAddKey(field, keyText); EnsureUniqueDictionaryKey(field, key);
        var entity = value is Stride.Engine.Entity target ? target : (value as Stride.Engine.EntityComponent)?.Entity;
        if (entity == null || !Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var part) || !ReferenceEquals(part.Entity, entity)
            || value is Stride.Engine.EntityComponent component && !entity.Components.Contains(component)) throw new ArgumentException("The reference must belong to this scene.");
        EndEdits(); InsertCollectionValue(field, value, new NodeIndex(key));
    }

    private static void EnsureUniqueDictionaryKey(ComponentField field, object key)
    {
        if (((DictionaryDescriptor)field.CollectionDescriptor).ContainsKey(field.Node.Retrieve()!, key))
            throw new ArgumentException("This dictionary already contains that key.");
    }

    internal void AddDictionaryAssetReference(ComponentField field, string keyText, object value)
    {
        if (!CanAddDictionaryKeys(field) || !ContentAssetReference.Supports(field.CollectionElementType!))
            throw new InvalidOperationException("This dictionary cannot be changed.");
        ContentAssetReference.Validate(field.CollectionElementType!, value, allowsNull: false);
        var key = CreateDictionaryAddKey(field, keyText); EnsureUniqueDictionaryKey(field, key);
        EndEdits(); InsertCollectionValue(field, value, new NodeIndex(key));
    }

    internal void RenameDictionaryKey(CollectionItem item, string keyText)
    {
        if (!CanEditDictionaryKeys(item.Field) || !CanEditHierarchyItem(item.Collection, item.Index)) throw new InvalidOperationException("This key cannot be changed.");
        var key = ParseDictionaryKey(item.Field, keyText); var previousIndex = item.Index;
        if (Equals(previousIndex.Value, key)) return;
        if (((DictionaryDescriptor)item.Field.CollectionDescriptor).ContainsKey(item.Collection.Retrieve()!, key)) return;
        EndEdits();
        var value = item.Value;
        var itemOverride = item.Collection.GetItemOverride(item.Index);
        var keyOverride = item.Collection.GetKeyOverride(item.Index);
        ApplyInheritedGraphMutation(() =>
        {
            var target = new NodeIndex(key);
            var inherited = item.Collection.BaseNode is Stride.Core.Assets.Quantum.IAssetObjectNode source && source.HasId(item.Id);
            if (inherited)
            {
                item.Collection.Remove(value!, item.Collection.IdToIndex(item.Id));
                item.Collection.Add(value!, target);
            }
            else
            {
                item.Collection.RemoveAndDiscard(value!, item.Collection.IdToIndex(item.Id), item.Id);
                item.Collection.Restore(value!, target, item.Id);
            }
            item.Collection.RestoreItemOverrides(target, itemOverride | (inherited ? OverrideType.New : OverrideType.Base),
                keyOverride | OverrideType.New);
        }, "Rename key: " + item.Field.Label);
    }
}
