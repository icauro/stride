using Stride.Assets.Materials;
using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private IAssetObjectNode MaterialLayerCollection
    {
        get { EnsureMaterialSettingsGraph(); return (IAssetObjectNode)materialSettingsGraph!.RootNode[nameof(MaterialAsset.Layers)].Target!; }
    }
    internal bool CanCopyMaterialLayer(MaterialSettingLayer layer)
        => MaterialSettings != null && MaterialLayerCollection.HasId(layer.Id);
    internal string CopyMaterialLayer(MaterialSettingLayer layer)
    {
        CommitEdits();
        if (!CanCopyMaterialLayer(layer)) throw new InvalidOperationException("This layer no longer exists.");
        var collection = MaterialLayerCollection;
        return CopyAssetPropertyAccessor(new(collection, collection.IdToIndex(layer.Id)));
    }
    internal bool CanPasteMaterialLayer(MaterialSettingLayer layer, string text)
        => CanCopyMaterialLayer(layer) && CanEditMaterialLayer(layer)
            && CreateAssetPropertyClipboard().CanPaste(text, MaterialLayerCollection.Type);
    internal void PasteMaterialLayer(MaterialSettingLayer layer, string text, bool replace)
    {
        CommitEdits();
        if (!CanPasteMaterialLayer(layer, text)) throw new InvalidOperationException("The clipboard cannot be pasted into this layer.");
        var collection = MaterialLayerCollection;
        var clipboard = CreateAssetPropertyClipboard();
        ApplyInheritedAssetMutation(() => clipboard.Paste(text, collection.Type, new(collection, collection.IdToIndex(layer.Id)), replace),
            replace ? "Replace property" : "Paste property");
    }
}
