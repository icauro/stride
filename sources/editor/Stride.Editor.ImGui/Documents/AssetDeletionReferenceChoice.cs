using Stride.Core.Assets;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

/// <summary>One real authored reference awaiting deletion-dialog resolution.</summary>
internal sealed class AssetDeletionReferenceChoice
{
    public Guid Id { get; } = Guid.NewGuid();
    public MemberPath Path { get; }
    public AssetId DeletedAssetId { get; }
    public object Before { get; }
    public Type StorageType { get; }
    public bool IsResolved { get; private set; }
    public bool IsClear { get; private set; }
    public AssetItem? Replacement { get; private set; }
    public object? After { get; private set; }
    private object? replacementValue;
    public AssetDeletionReferenceChoice(MemberPath path, AssetId deleted, object before)
    {
        Path = path; DeletedAssetId = deleted; Before = before;
        var last = path.Decompose().Last();
        StorageType = last switch
        {
            MemberPath.ArrayPathItem item => item.Descriptor.ElementType,
            MemberPath.CollectionPathItem item => item.Descriptor.ElementType,
            MemberPath.DictionaryPathItem item => item.Descriptor.ValueType,
            MemberPath.SetPathItem item => item.Descriptor.ElementType,
            _ => last.TypeDescriptor?.Type ?? before.GetType(),
        };
    }
    public bool Accepts(AssetItem asset) => ContentAssetReference.Accepts(StorageType, asset);
    public void Clear()
    {
        ContentAssetReference.Validate(StorageType, null, !StorageType.IsValueType || Nullable.GetUnderlyingType(StorageType) != null);
        IsResolved = IsClear = true; After = null;
    }
    public void SetReplacement(AssetItem asset)
    {
        var value = ContentAssetReference.Create(StorageType, asset);
        Replacement = asset; After = replacementValue = value; IsResolved = true; IsClear = false;
    }
    public void UseReplacement()
    { IsClear = false; After = replacementValue; IsResolved = Replacement != null; }
    public void Reset() { Replacement = null; After = replacementValue = null; IsResolved = IsClear = false; }
}
