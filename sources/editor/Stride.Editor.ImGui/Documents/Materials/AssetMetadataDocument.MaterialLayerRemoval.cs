using Stride.Assets.Materials;
using Stride.Core.Annotations;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal bool CanRemoveMaterialLayer(MaterialSettingLayer layer)
    {
        if (!CanEditMaterialLayer(layer)) return false;
        var member = AuthoringGraph.RootNode[nameof(MaterialAsset.Layers)];
        var rules = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<MemberCollectionAttribute>(member.MemberDescriptor.MemberInfo);
        return rules?.ReadOnly != true && !IsExtensionPropertyReadOnly(member) && NativePropertyClipboard.HasRemove(MaterialLayerCollection);
    }
}
