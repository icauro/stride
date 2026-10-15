using Stride.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal bool CanEditPresentedGameSettingItem(GameSettingItem item)
        => gameSettingItemMembers.Contains(item) && CanEditGameSettingCollection(item.Context)
            && (item.Id is not { } id || item.Collection.HasId(id))
            && CanEditAssetSettingItem(item.Collection, item.Index) && !IsExtensionItemReadOnly(item.Collection, item.Index);
    internal bool IsPresentedGameSettingItemVisible(GameSettingItem item) => IsExtensionItemVisible(item.Collection, item.Index);
    internal string PresentedGameSettingItemLabel(GameSettingItem item) => ExtensionItemLabel(item.Collection, item.Index) ?? item.DisplayName;
    private IAssetObjectNode GameConfigurationNode()
    {
        EnsureGameSettingsGraph();
        return (IAssetObjectNode)gameSettingsGraph!.RootNode[nameof(GameSettingsAsset.Defaults)].Target!;
    }
    private NodeIndex GameConfigurationIndex(GameSettingConfiguration configuration)
        => configuration.Id is { } id ? GameConfigurationNode().IdToIndex(id) : configuration.Index;
    internal bool IsPresentedGameConfigurationVisible(GameSettingConfiguration configuration)
        => IsExtensionItemVisible(GameConfigurationNode(), GameConfigurationIndex(configuration));
    internal string PresentedGameConfigurationLabel(GameSettingConfiguration configuration)
        => configuration.Label;
    internal bool CanEditPresentedGameConfiguration(GameSettingConfiguration configuration)
    {
        if (!CanEditGameSettings || !TryGetGameConfigurationAccessor(configuration, out var accessor)) return false;
        var node = (IAssetObjectNode)accessor.Node;
        return CanEditAssetSettingItem(node, accessor.Index) && !IsExtensionItemReadOnly(node, accessor.Index);
    }
}
