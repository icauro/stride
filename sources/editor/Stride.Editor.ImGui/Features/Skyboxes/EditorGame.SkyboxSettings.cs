using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawSkyboxSettings(AssetMetadataDocument settings)
    {
        if (settings.SkyboxSettings == null) return;
        Gui.PushID("SkyboxSettings");
        DrawPresentedAssetRows(settings, settings.SkyboxSettingsFields.Where(settings.IsSkyboxSettingVisible).Select(field =>
            new PresentedAssetRow(field.Node, "", settings.SkyboxSettingOrder(field), Stride.Core.ExpandRule.Always, () => DrawSkyboxSetting(settings, field))));
        Gui.PopID();
    }
    private void DrawSkyboxSetting(AssetMetadataDocument settings, SkyboxSettingField field)
    {
        Gui.PushID(field.Node.Name);
        try
        {
            if (settings.ReadSkyboxSetting(field) is not Stride.Core.Yaml.IUnloadable && TryDrawRegisteredSkyboxProperty(settings, field))
            { DrawAssetSettingResetToBase(settings, field.Node); return; }
            DrawAssetSettingLabel(settings, field.Node, settings.SkyboxSettingLabel(field)); Gui.SetNextItemWidth(-1);
            var entries = settings.SkyboxSettingEntries(field);
            var factory = entries != null && settings.HasSkyboxSettingFactory(field) && !settings.IsSkyboxSettingReadOnly(field);
            if (factory && Gui.BeginTable("##FactoryRow", 2, ImGuiTableFlags.SizingFixedFit))
            {
                try
                {
                    Gui.TableSetupColumn("##Value", ImGuiTableColumnFlags.WidthStretch, 1f);
                    Gui.TableSetupColumn("##Factory", ImGuiTableColumnFlags.WidthFixed, UiScale(150));
                    Gui.TableNextColumn(); Gui.SetNextItemWidth(-1); DrawSkyboxSettingValue(settings, field);
                    Gui.TableNextColumn(); Gui.SetNextItemWidth(-1);
                    using var disabled = ImGuiScopes.Disabled(!CanEditSkyboxSettingValue(settings, field));
                    if (Gui.BeginCombo("##Factory", NativeAbstractEntryControl.Label(entries!, settings.ReadSkyboxSetting(field))))
                    {
                        try
                        {
                            NativeAbstractEntryControl.Choices(entries!, settings.ReadSkyboxSetting(field), entry =>
                            {
                                if (!CanEditSkyboxSettingValue(settings, field) || !settings.HasSkyboxSettingFactory(field)) return;
                                UseTextureSettingsCommandContext();
                                SelectNativeFactoryEntry(settings, field.Node, entry, () => settings.SkyboxSettingEntries(field) ?? [],
                                    () => throw new InvalidOperationException("The skybox factory command is no longer attached."));
                            });
                        }
                        finally { Gui.EndCombo(); }
                    }
                }
                finally { Gui.EndTable(); }
            }
            else DrawSkyboxSettingValue(settings, field);
            DrawAssetSettingResetToBase(settings, field.Node);
        }
        finally { Gui.PopID(); }
    }
    private bool CanEditSkyboxSettingValue(AssetMetadataDocument settings, SkyboxSettingField field)
        => !IsAuthoringPipelineUpdating && IsCurrentMetadataControlOwner(settings) && settings.SkyboxSettingsFields.Contains(field)
        && settings.CanEditSkyboxSettings && field.CanAssign && settings.CanEditAssetSettingNode(field.Node) && !settings.IsSkyboxSettingReadOnly(field);
    private void DrawSkyboxSettingValue(AssetMetadataDocument settings, SkyboxSettingField field)
    {
            if (settings.ReadSkyboxSetting(field) is Stride.Core.Yaml.IUnloadable unknown) { DrawUnloadableProperty(unknown); return; }
            if (AssetSettingNumericMetadata.Supports(field.Type)) { DrawSkyboxNumberDraft(settings, field); return; }
            var canEdit = CanEditSkyboxSettingValue(settings, field);
            var nativeReference = ContentAssetReference.Supports(field.Type);
            Gui.BeginDisabled(!canEdit && !nativeReference); var value = settings.ReadSkyboxSetting(field);
            if (ContentAssetReference.Supports(field.Type))
                DrawContentReferenceValue(field.Type, value, field.AllowsNull, updated => Apply(updated, true), canEdit: canEdit);
            else if (field.Type == typeof(bool))
            {
                var next = (bool)(value ?? false); if (Gui.Checkbox("##Value", ref next)) Apply(next, true);
            }
            else if (field.Type.IsEnum)
            {
                if (icons!.HasEnumImages(field.Type))
                {
                    if (NativeEnumImageControl.Draw(icons, field.Node, field.Type, value, false,
                        field.EnumOptions.Select(option => (Enum)option.Value), UiScale(1), out var chosen)) Apply(chosen, true);
                }
                else if (Gui.BeginCombo("##Value", field.EnumOptions.FirstOrDefault(option => Equals(option.Value, value))?.Label ?? value?.ToString()))
                {
                    foreach (var option in field.EnumOptions)
                        if (Gui.Selectable(option.Label, Equals(option.Value, value))) Apply(option.Value, true);
                    Gui.EndCombo();
                }
            }
            Gui.EndDisabled();
            void Apply(object? updated, bool end = false)
            {
                UseTextureSettingsCommandContext();
                Execute(() => { ValidatePendingPropertyStrings(); settings.UpdateSkyboxSetting(field, updated); if (end) settings.EndSkyboxSettingEdit(); });
            }
    }
}
