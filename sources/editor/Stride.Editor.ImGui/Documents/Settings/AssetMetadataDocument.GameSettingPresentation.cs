namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal bool IsPresentedGameSettingVisible(GameSettingField field) => IsCurrentGameSettingField(field) && IsExtensionPropertyVisible(field.Node);
    internal string PresentedGameSettingLabel(GameSettingField field) => ExtensionPropertyLabel(field.Node) ?? field.Label;
    internal bool CanEditPresentedGameSetting(GameSettingField field)
        => IsCurrentGameSettingField(field) && CanEditGameSettings && field.CanAssign && CanEditAssetSettingNode(field.Node) && !IsExtensionPropertyReadOnly(field.Node);
    internal bool IsPresentedGameSettingCollectionReadOnly(GameSettingCollection collection)
        => IsExtensionPropertyReadOnly(collection.Origin.Node);
}
