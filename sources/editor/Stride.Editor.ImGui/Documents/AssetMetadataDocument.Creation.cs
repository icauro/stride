using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal AssetMetadataDocument(string path, AssetItem item, EditorSessionHistory history)
        : this(path, new AssetLoadResult<Asset>(item.Asset, null, false, item.YamlMetadata), [], history, false) { }
}
