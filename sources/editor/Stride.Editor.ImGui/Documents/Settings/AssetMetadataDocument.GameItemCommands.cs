using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal EditorPresenterTarget? AcquireGameItemCommandTarget(GameSettingItem item)
    {
        if (!CanCopyGameSettingItem(item)) return null;
        // Use the selected-root adapter too: VisualScript block inspectors
        // share these fields, but must retain their actual block presenters.
        return AcquireExtensionFactoryTarget(new NodeAccessor(item.Collection, item.Index));
    }
}
