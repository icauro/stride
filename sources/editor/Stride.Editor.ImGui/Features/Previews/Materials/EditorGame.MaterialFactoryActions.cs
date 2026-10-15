using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void ReplaceMaterialSettingEntry(AssetMetadataDocument owner, MaterialSettingField field, AbstractNodeEntry entry)
    {
        if (!owner.CanChangeMaterialSettingEntry(field) || IsAuthoringPipelineUpdating)
            throw new InvalidOperationException("This material property cannot be replaced.");
        owner.EndMaterialSettingEdit();
        SelectNativeFactoryEntry(owner, field.Node, entry, () => owner.MaterialSettingEntries(field),
            () => owner.ChangeMaterialSettingEntry(field, entry));
    }
}
