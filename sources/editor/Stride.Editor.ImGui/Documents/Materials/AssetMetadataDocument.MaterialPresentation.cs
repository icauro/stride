using Stride.Core;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal IEnumerable<string> MaterialShaderNames(IMemberNode node)
        => ExtensionAssetPresentation?.ShaderNames(node) ?? MaterialShaderSources?.Invoke().Keys ?? Enumerable.Empty<string>();
    internal bool IsMaterialSettingVisible(MaterialSettingField field) => IsExtensionPropertyVisible(field.Node);
    internal bool IsMaterialSettingReadOnly(MaterialSettingField field) => IsExtensionPropertyReadOnly(field.Node);
    internal string MaterialSettingLabel(MaterialSettingField field) => ExtensionPropertyLabel(field.Node) ?? field.Label;
    internal PropertyNumericRange? MaterialSettingRange(MaterialSettingField field) => ExtensionAssetPresentation?.NumericRange(field.Node) ?? field.NumericRange;
    internal ExpandRule MaterialSettingExpansion(MaterialSettingField field) => ExtensionAssetPresentation?.Expansion(field.Node) ?? ExpandRule.Auto;
    internal int? MaterialSettingOrder(MaterialSettingField field) => ExtensionAssetPresentation?.Order(field.Node);
    internal MaterialSettingField? InlinedMaterialSetting(MaterialSettingField field)
    {
        var node = ExtensionAssetPresentation?.InlineMember(field.Node);
        return node == null ? null : field.Children.FirstOrDefault(child => ReferenceEquals(child.Node, node));
    }
    internal MaterialSettingField? InlinedMaterialLayerSetting(MaterialSettingLayer layer)
    {
        var collection = MaterialLayerCollection;
        if (!collection.HasId(layer.Id)) return null;
        var presenter = ExtensionAssetPresentation?.ItemPresenter(collection, collection.IdToIndex(layer.Id));
        var accessor = presenter?.TryGetChild(Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys.InlineData.InlinedProperty)?.GetNodeAccessor();
        return accessor?.Node is IMemberNode member ? layer.Fields.FirstOrDefault(field => ReferenceEquals(field.Node, member)) : null;
    }
    internal bool IsMaterialLayerVisible(MaterialSettingLayer layer)
    {
        var collection = MaterialLayerCollection;
        return collection.HasId(layer.Id) && IsExtensionItemVisible(collection, collection.IdToIndex(layer.Id));
    }
    internal string MaterialLayerLabel(MaterialSettingLayer layer, int index)
    {
        var collection = MaterialLayerCollection;
        var presenter = collection.HasId(layer.Id) ? ExtensionAssetPresentation?.ItemPresenter(collection, collection.IdToIndex(layer.Id)) : null;
        return presenter?.DisplayName ?? (string.IsNullOrWhiteSpace(layer.Value.Name) ? "Layer " + index : layer.Value.Name);
    }
    internal bool IsSkyboxSettingVisible(SkyboxSettingField field) => IsExtensionPropertyVisible(field.Node);
    internal string SkyboxSettingLabel(SkyboxSettingField field) => ExtensionPropertyLabel(field.Node) ?? field.Label;
    internal bool IsSkyboxSettingReadOnly(SkyboxSettingField field) => IsExtensionPropertyReadOnly(field.Node);
    internal PropertyNumericRange? SkyboxSettingRange(SkyboxSettingField field) => ExtensionAssetPresentation?.NumericRange(field.Node) ?? field.Range;
    internal int? SkyboxSettingOrder(SkyboxSettingField field) => ExtensionAssetPresentation?.Order(field.Node);
}
