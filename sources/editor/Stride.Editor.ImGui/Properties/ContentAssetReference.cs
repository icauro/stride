using Stride.Core.Assets;
using Stride.Core.Serialization;
using Reference = Stride.Core.Serialization.Contents.IReference;

namespace Stride.Editor.ImGui;

internal static class ContentAssetReference
{
    internal static bool Supports(Type type) => AssetRegistry.CanBeAssignedToContentTypes(type, checkIsUrlType: true);
    internal static Reference? Read(object? value) => value as Reference ?? AttachedReferenceManager.GetAttachedReference(value);
    internal static bool AreEqual(object? left, object? right)
    {
        if (left != null && right != null && left.GetType() == right.GetType() && AssetRegistry.IsExactContentType(left.GetType()))
        {
            var a = AttachedReferenceManager.GetAttachedReference(left); var b = AttachedReferenceManager.GetAttachedReference(right);
            if (a != null && b != null && a.Id == b.Id && a.Url == b.Url) return true;
        }
        return Equals(left, right);
    }

    internal static bool Accepts(Type type, AssetItem asset) => AssetRegistry.CanPropertyHandleAssets(type, out var accepted)
        && (typeof(AssetReference).IsAssignableFrom(type) && accepted.Count == 0
            || accepted.Any(candidate => candidate.IsInstanceOfType(asset.Asset)));

    internal static object Create(Type type, AssetItem asset)
    {
        if (!Accepts(type, asset)) throw new ArgumentException("The asset is not compatible with this property.");
        object value;
        if (UrlReferenceBase.IsUrlReferenceType(type)) value = UrlReferenceBase.New(type, asset.Id, asset.Location.ToString());
        else if (typeof(AssetReference).IsAssignableFrom(type)) value = new AssetReference(asset.Id, asset.Location);
        else
        {
            var contentType = AssetRegistry.GetContentType(asset.Asset.GetType());
            if (contentType == null || !type.IsAssignableFrom(contentType)) throw new ArgumentException("The asset content type is not compatible with this property.");
            value = AttachedReferenceManager.CreateProxyObject(contentType, asset.Id, asset.Location.ToString());
        }
        if (!type.IsInstanceOfType(value)) throw new ArgumentException("The asset reference type is not compatible with this property.");
        return value;
    }

    internal static void Validate(Type type, object? value, bool allowsNull)
    {
        if (value == null) { if (!allowsNull) throw new ArgumentException("This property cannot contain null."); return; }
        var reference = Read(value);
        if (!type.IsInstanceOfType(value) || reference == null || reference.Id == AssetId.Empty || string.IsNullOrWhiteSpace(reference.Location))
            throw new ArgumentException("The property requires a compatible asset reference.");
    }
}
