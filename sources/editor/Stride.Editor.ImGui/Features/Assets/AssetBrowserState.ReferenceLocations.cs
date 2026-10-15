using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class AssetBrowserState
{
    internal AssetBrowserFolder? FolderForAsset(AssetId id) => assetFolders.GetValueOrDefault(id);
}
