using Hexa.NET.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawSpriteNameDraft(AssetMetadataDocument owner, SpriteSettingField field, string control = "##Value", bool autoSelect = false)
    {
        try
        {
            PropertyGrid.Strings.Draw(field.Node, false, () => CanEditSpriteSettingDraft(owner, field),
                () => ReadSpriteSettingDraft(owner, field), text => CommitSpriteSettingDraft(owner, field, text),
                () => AcquireSpriteSettingDraft(owner, field), controlLabel: control,
                input: (string id, string hint, ref string text, ImGuiInputTextFlags flags)
                    => NativeStringInput.DrawWithHint(id, hint, ref text, autoSelect ? flags | ImGuiInputTextFlags.AutoSelectAll : flags));
            if (Controls.Enabled && field.Node.Name == "Name") Controls.Record("sprites/name");
        }
        catch (Exception error) { statusMessage = error.Message; }
    }
    private void DrawSpriteCommonNameDraft(AssetMetadataDocument owner, SpriteCommonSetting setting)
    {
        var mixed = setting.Fields.Skip(1).Any(field => !Equals(field.Node.Retrieve(), setting.First.Node.Retrieve()));
        try
        {
            PropertyGrid.Strings.Draw(setting.First.Node, mixed, () => CanEditSpriteCommonNumericDraft(owner, setting),
                () => ReadSpriteCommonNumericDraft(owner, setting), text =>
                {
                    if (!CanEditSpriteCommonNumericDraft(owner, setting)) throw new InvalidOperationException("The sprite selection is no longer editable.");
                    if (setting.Fields.All(field => Equals(field.Node.Retrieve(), text))) return;
                    UseTextureSettingsCommandContext();
                    owner.UpdateSpriteCommonValue(setting, _ => text); owner.EndSpriteCommonEdit();
                }, () => AcquireSpriteCommonNumericDraft(owner, setting));
            if (Controls.Enabled && setting.First.Node.Name == "Name") Controls.Record("sprites/name");
        }
        catch (Exception error) { statusMessage = error.Message; }
    }
}
