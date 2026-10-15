using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private bool TryGetGameConfigurationAccessor(GameSettingConfiguration configuration, out NodeAccessor accessor)
    {
        accessor = default;
        EnsureGameSettingsGraph();
        // A successful pending-field commit can rebuild descriptors for this
        // same item. Certify native identity/value, not the old Fields array.
        if (!gameSettingConfigurations.Any(current => ReferenceEquals(current.Value, configuration.Value)
            && (configuration.Id is { } itemId ? current.Id == itemId : current.Id == null && current.Index == configuration.Index))) return false;
        var node = GameConfigurationNode();
        if (configuration.Id is { } id && !node.HasId(id)) return false;
        var index = GameConfigurationIndex(configuration);
        if (node.Indices?.Contains(index) != true || !ReferenceEquals(node.Retrieve(index), configuration.Value)) return false;
        accessor = new(node, index);
        return true;
    }
    internal bool CanCopyGameConfiguration(GameSettingConfiguration configuration)
        => TryGetGameConfigurationAccessor(configuration, out _);
    internal object?[] GameConfigurationSource(GameSettingConfiguration configuration)
    {
        if (!TryGetGameConfigurationAccessor(configuration, out var accessor))
            throw new InvalidOperationException("The authored game configuration is no longer available.");
        return [gameSettingsGraph, accessor.Node, configuration.Id, accessor.Index, accessor.RetrieveValue(),
            configuration.Value.GetType(), Path];
    }
    private GameSettingItem? CurrentGameConfigurationItem(GameSettingConfiguration configuration)
    {
        if (!TryGetGameConfigurationAccessor(configuration, out var accessor)) return null;
        var field = gameSettingFields.FirstOrDefault(member => member.Node.Name == nameof(Stride.Assets.GameSettingsAsset.Defaults));
        return field == null ? null : GetGameSettingItems(field).FirstOrDefault(item => ReferenceEquals(item.Collection, accessor.Node)
            && item.Index == accessor.Index && ReferenceEquals(item.Value, configuration.Value));
    }
    internal bool CanRemoveGameConfiguration(GameSettingConfiguration configuration)
        => CurrentGameConfigurationItem(configuration) is { } item && CanRemoveGameSettingItem(item);
    internal string CopyGameConfiguration(GameSettingConfiguration configuration)
    {
        CommitEdits();
        if (!TryGetGameConfigurationAccessor(configuration, out var accessor))
            throw new InvalidOperationException("The authored game configuration is no longer available.");
        return CopyAssetPropertyAccessor(accessor);
    }
    internal bool CanPasteGameConfiguration(GameSettingConfiguration configuration, string text)
        => TryGetGameConfigurationAccessor(configuration, out var accessor) && CanPasteAssetAccessors([accessor], text);
    internal void PasteGameConfiguration(GameSettingConfiguration configuration, string text, bool replace)
    {
        CommitEdits();
        if (!TryGetGameConfigurationAccessor(configuration, out var accessor) || !CanPasteAssetAccessors([accessor], text))
            throw new InvalidOperationException("The clipboard cannot be pasted into this game configuration.");
        PasteAssetAccessors([accessor], text, replace);
    }
    internal bool CanResetGameConfiguration(GameSettingConfiguration configuration)
        => TryGetGameConfigurationAccessor(configuration, out var accessor)
            && CanResetAssetSettingItem((Stride.Core.Assets.Quantum.IAssetObjectNode)accessor.Node, accessor.Index);
    internal void ResetGameConfiguration(GameSettingConfiguration configuration)
    {
        CommitEdits();
        if (!TryGetGameConfigurationAccessor(configuration, out var accessor))
            throw new InvalidOperationException("The authored game configuration is no longer available.");
        ResetAssetSettingItem((Stride.Core.Assets.Quantum.IAssetObjectNode)accessor.Node, accessor.Index);
    }
    internal EditorPresenterTarget? AcquireGameConfigurationCommandTarget(GameSettingConfiguration configuration)
        => TryGetGameConfigurationAccessor(configuration, out var accessor) ? AcquireExtensionFactoryTarget(accessor) : null;
    internal bool IsGameConfigurationCommandTargetCurrent(GameSettingConfiguration configuration, EditorPresenterTarget target)
    {
        if (!TryGetGameConfigurationAccessor(configuration, out var accessor)) return false;
        var actual = target.Presenter.GetNodeAccessor();
        return ReferenceEquals(actual.Node, accessor.Node) && actual.Index == accessor.Index;
    }
}
