using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal IReadOnlyList<AbstractNodeEntry>? SkyboxSettingEntries(SkyboxSettingField field)
        => ExtensionAbstractEntries(field.Node)?.OrderBy(entry => entry.Order).ToArray();
    internal bool HasSkyboxSettingFactory(SkyboxSettingField field)
        => SkyboxSettingsFields.Contains(field) && ExtensionAssetPresentation?.Find(field.Node)?.Commands
            .Any(command => command.Name == "CreateNewInstance") == true;
}
