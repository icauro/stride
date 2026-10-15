using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawImportTemporalDraft(AssetMetadataDocument owner, AssetImportSettingField field)
    {
        DrawRetainedTemporal(field.Node, field.Type,
            () => CanEditImportSettingDraft(owner, field) && owner.ReadImportSetting(field) != null,
            () => ReadImportSettingDraft(owner, field),
            transform => CommitImportSettingDraft(owner, field, transform(field.Node.Retrieve())), 1,
            () => AcquireImportSettingDraft(owner, field));
        if (Controls.Enabled && Gui.IsItemVisible()) Controls.Record("asset-settings/" + field.Key);
    }
}
