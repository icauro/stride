using System.Collections;
using Stride.Core.Assets.Visitors;
using Stride.Core.Reflection;
using Stride.Core.Serialization;

namespace Stride.Editor.ImGui;

/// <summary>Actual serialized runtime reference paths; compiled resource internals are leaves.</summary>
internal sealed class ImportedRuntimeReferences : AssetVisitorBase
{
    internal readonly record struct Reference(MemberPath Path, object Value, Type StorageType, bool CanWrite);
    public List<Reference> References { get; } = [];
    private bool ReferenceValue(object? value, Type storageType, bool canWrite = true)
    {
        if (value == null || AttachedReferenceManager.GetAttachedReference(value) == null) return false;
        References.Add(new(CurrentPath.Clone(), value, storageType, canWrite)); return true;
    }
    public override void VisitObject(object obj, ObjectDescriptor descriptor, bool visitMembers)
    {
        if (AttachedReferenceManager.GetAttachedReference(obj) == null) base.VisitObject(obj, descriptor, visitMembers);
    }
    public override void VisitObjectMember(object container, ObjectDescriptor descriptor, IMemberDescriptor member, object? value)
    { if (!ReferenceValue(value, member.Type, member.HasSet)) base.VisitObjectMember(container, descriptor, member, value); }
    public override void VisitArrayItem(Array array, ArrayDescriptor descriptor, int index, object? item, ITypeDescriptor? itemDescriptor)
    { if (!ReferenceValue(item, descriptor.ElementType)) base.VisitArrayItem(array, descriptor, index, item, itemDescriptor); }
    public override void VisitCollectionItem(IEnumerable collection, CollectionDescriptor descriptor, int index, object? item, ITypeDescriptor? itemDescriptor)
    { if (!ReferenceValue(item, descriptor.ElementType, descriptor.HasIndexerAccessors)) base.VisitCollectionItem(collection, descriptor, index, item, itemDescriptor); }
    public override void VisitDictionaryKeyValue(object dictionary, DictionaryDescriptor descriptor, object key, ITypeDescriptor? keyDescriptor, object? value, ITypeDescriptor? valueDescriptor)
    { if (!ReferenceValue(value, descriptor.ValueType, !descriptor.IsReadOnly(dictionary))) base.VisitDictionaryKeyValue(dictionary, descriptor, key, keyDescriptor, value, valueDescriptor); }
    public override void VisitSetItem(IEnumerable set, SetDescriptor descriptor, object? item, ITypeDescriptor? itemDescriptor)
    { if (!ReferenceValue(item, descriptor.ElementType, false)) base.VisitSetItem(set, descriptor, item, itemDescriptor); }
}
