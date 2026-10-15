using Stride.Assets.Materials;
using Stride.Core.Assets.Quantum;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal EditorPresenterTarget? AcquireMaterialLayerCommandTarget(MaterialSettingLayer layer)
    {
        EnsureMaterialSettingsGraph();
        var layers = materialSettingsGraph!.RootNode[nameof(MaterialAsset.Layers)].Target as IAssetObjectNode;
        if (layers == null || !layers.HasId(layer.Id)) return null;
        var index = layers.IdToIndex(layer.Id);
        if (!ReferenceEquals(layers.Retrieve(index), layer.Value)) return null;
        var presentation = ExtensionAssetPresentation;
        var presenter = presentation?.ItemPresenter(layers, index);
        if (presenter == null || !layers.HasId(layer.Id) || layers.IdToIndex(layer.Id) != index
            || !ReferenceEquals(layers.Retrieve(index), layer.Value)) return null;
        return presentation!.AcquireTarget(presenter);
    }
}
