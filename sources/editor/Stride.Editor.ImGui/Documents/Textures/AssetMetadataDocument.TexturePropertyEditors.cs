using Stride.Assets.Textures;
using Stride.Core.Assets;
using Stride.Core.Annotations;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal void ReplaceTextureTypeValue(object? value)
    {
        EnsureTextureSettingsGraph();
        if (!CanChangeTextureTypeEntry() || value is not ITextureType)
            throw new InvalidOperationException("The texture type cannot be replaced with this value.");
        var node = TextureTypeSettingNode!;
        if (ReferenceEquals(node.Retrieve(), value)) return;
        ApplyInheritedAssetMutation(() =>
        {
            if (!CanChangeTextureTypeEntry()) throw new InvalidOperationException("The texture type became read-only.");
            AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
            node.Update(value);
        }, "Edit texture setting: " + node.Name);
    }
}
