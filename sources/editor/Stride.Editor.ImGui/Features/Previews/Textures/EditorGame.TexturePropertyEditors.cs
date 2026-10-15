namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool TryDrawRegisteredTextureSetting(AssetMetadataDocument owner, TextureSettingField field, string label)
    {
        object?[] Target() => [.. CertifiedPropertyTarget(owner, field.Node), field.Key, owner.TextureSettings?.Type, owner.TextureSettings?.IsSizeInPercentage];
        return TryDrawCertifiedPropertyEditor(owner, field.Node, label,
            () => IsTextureSettingCurrent(owner, field), () => CanEditRetainedTextureSetting(owner, field), () => field.Node.Retrieve(),
            () => [.. Target(), owner.EditRevision], Target, value => owner.UpdateTextureSetting(field, value!), owner.EndTextureSettingEdit);
    }
    private bool TryDrawRegisteredTextureType(AssetMetadataDocument owner, Stride.Core.Quantum.IMemberNode node)
    {
        bool Current() => IsCertifiedPropertyOwner(owner) && ReferenceEquals(owner.TextureTypeSettingNode, node);
        object?[] Target() => [.. CertifiedPropertyTarget(owner, node), owner.TextureSettings?.IsSizeInPercentage];
        return TryDrawCertifiedPropertyEditor(owner, node, owner.ExtensionPropertyLabel(node) ?? "Type", Current,
            owner.CanChangeTextureTypeEntry, node.Retrieve, () => [.. Target(), owner.EditRevision], Target,
            value => owner.ReplaceTextureTypeValue(value), () => { });
    }
}
