using Stride.Assets.Presentation.NodePresenters.Rules;
using Stride.Assets.Rendering;
using Stride.Rendering.Compositing;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    // Original CameraSlotNodeUpdater includes compositors in this package, excluding dependencies.
    public IEnumerable<CameraSlotChoice> GetCompositorCameraSlotChoices()
    {
        var package = authoringItem?.Package ?? SessionHistory?.HierarchyGraphs.ResolveAssetItem?.Invoke(AuthoredAsset.Id)?.Package;
        if (package == null)
        {
            var index = 0;
            foreach (var slot in CompositorAsset!.Cameras) yield return new(slot.ToSlotId(), slot.Name, index++);
            yield break;
        }
        foreach (var item in package.Assets)
        {
            var asset = (SessionHistory?.Registry.Find(item.Id)?.Document as AssetMetadataDocument)?.CompositorAsset ?? item.Asset as GraphicsCompositorAsset;
            if (asset == null) continue;
            var index = 0;
            foreach (var slot in asset.Cameras) yield return new(slot.ToSlotId(), item.Location + " > " + slot.Name, index++);
        }
    }
}
