using Stride.Core.Assets;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal IReadOnlyList<AbstractNodeEntry> MaterialSettingEntries(MaterialSettingField field)
    {
        var entries = ExtensionAssetPresentation?.AbstractEntries(field.Node);
        return entries?.OrderBy(entry => entry.Order).ThenBy(entry => entry.DisplayValue, StringComparer.CurrentCulture).ToArray() ?? [];
    }
    internal bool CanChangeMaterialSettingEntry(MaterialSettingField field)
        => CanEditMaterialSettings && IsCurrentMaterialField(field) && field.CanAssign && CanEditAssetSettingNode(field.Node) && !IsMaterialSettingReadOnly(field);
    internal void ChangeMaterialSettingEntry(MaterialSettingField field, AbstractNodeEntry entry)
    {
        EndMaterialSettingEdit();
        if (!CanChangeMaterialSettingEntry(field))
            throw new InvalidOperationException("This material property cannot be replaced.");
        var available = MaterialSettingEntries(field);
        if (!available.Any(candidate => candidate.Equals(entry) || candidate is AbstractNodeTypeGroup group && group.Types.Any(candidateType => candidateType.Equals(entry))))
            throw new InvalidOperationException("The material entry is no longer registered for this property.");
        if (entry is AbstractNodeTypeGroup) throw new InvalidOperationException("A material type group cannot be assigned.");
        var current = field.Node.Retrieve();
        if (entry.IsMatchingValue(current!)) return;
        var value = entry.GenerateValue(current!);
        if (value != null) AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
        UpdateMaterialSetting(field, value);
        EndMaterialSettingEdit(); RefreshMaterialSettingsFields();
    }
}
