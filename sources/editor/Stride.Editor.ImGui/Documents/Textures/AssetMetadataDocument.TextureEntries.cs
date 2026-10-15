using Stride.Assets.Textures;
using Stride.Core.Assets;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal IReadOnlyList<AbstractNodeEntry> TextureTypeEntries()
    {
        EnsureTextureSettingsGraph();
        var node = TextureTypeSettingNode;
        return node == null ? [] : NativeAbstractEntries.Available(ExtensionAbstractEntries(node), TextureTypes, false);
    }
    internal bool CanChangeTextureTypeEntry()
        => CanEditArchetypeAsset && TextureSettings != null && TextureTypeSettingNode is { } node && CanEditAssetSettingNode(node)
        && CanAssignAuthoringNode(node) && !IsExtensionPropertyReadOnly(node);
    internal void ChangeTextureTypeEntry(AbstractNodeEntry entry)
    {
        EnsureTextureSettingsGraph(); EndTextureSettingEdit();
        if (!CanChangeTextureTypeEntry() || !NativeAbstractEntries.Contains(TextureTypeEntries(), entry))
            throw new InvalidOperationException("This texture type entry is no longer available.");
        var node = TextureTypeSettingNode!;
        if (entry.IsMatchingValue(node.Retrieve()!)) return;
        textureSettingMutation = BeginExtensionCommandMutation();
        textureSettingMutationSettled = false; textureSettingEditTitle = "Edit texture setting: " + node.Name;
        try
        {
            if (!CanChangeTextureTypeEntry() || !NativeAbstractEntries.Contains(TextureTypeEntries(), entry))
                throw new InvalidOperationException("This texture type entry is no longer available.");
            var value = entry.GenerateValue(node.Retrieve()!);
            if (value is not ITextureType) throw new ArgumentException("The texture type entry has an incompatible value.");
            AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
            node.Update(value);
            EndTextureSettingEdit();
        }
        catch (Exception failure)
        {
            try { CancelTextureSettingEdit(); }
            catch (Exception recovery) { throw new AggregateException("Texture changes and restoration failed.", failure, recovery); }
            throw;
        }
    }
}
