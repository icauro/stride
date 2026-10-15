using Stride.Assets.Rendering;
using Stride.Core.Assets.Quantum;
using Stride.Rendering;
using Stride.Rendering.Compositing;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    public bool CanRenameCompositorListItem(CompositorListItem item)
    {
        if (!CanEditCompositor || item.Value is not RenderStage and not SceneCameraSlot
            || AuthoringGraph.RootNode[item.Collection].Target is not IAssetObjectNode collection || !collection.HasId(item.ItemId)) return false;
        if (!ReferenceEquals(collection.Retrieve(collection.IdToIndex(item.ItemId)), item.Value)) return false;
        var member = item.Node[nameof(RenderStage.Name)];
        return CanEditAssetSettingNode(member) && CanAssignAuthoringNode(member) && !IsExtensionPropertyReadOnly(member);
    }
    public void RenameCompositorListItem(CompositorListItem item, string name, string expectedName)
    {
        CommitEdits();
        if (!CanRenameCompositorListItem(item) || item.Name != expectedName)
            throw new InvalidOperationException("This compositor item changed while its name was being edited.");
        if (name == expectedName) return;
        ApplyInheritedAssetMutation(() => item.Node[nameof(RenderStage.Name)].Update(name),
            item.Value is RenderStage ? "Rename render stage" : "Rename camera slot");
    }
}
