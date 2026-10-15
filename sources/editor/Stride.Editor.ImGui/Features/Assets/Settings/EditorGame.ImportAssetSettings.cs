using Hexa.NET.ImGui;
using Stride.Assets.Media;
using Stride.Core.IO;
using Stride.Core;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawImportAssetSettings(AssetMetadataDocument settings)
    {
        if (!settings.HasImportSettings) return;
        DrawPresentedImportAssetFields(settings, settings.ImportSettingsFields);
    }
    private void DrawPresentedImportAssetFields(AssetMetadataDocument settings, IReadOnlyList<AssetImportSettingField> fields)
        => DrawPresentedAssetRows(settings, fields.Select(field => new PresentedAssetRow(field.Node, field.Category, field.Order,
            field.CategoryOrder?.Expand ?? ExpandRule.Always, () => DrawImportAssetSetting(settings, field), field.CategoryOrder?.Order)));
    private void DrawImportAssetSetting(AssetMetadataDocument settings, AssetImportSettingField field, bool forceVisible = false, string? labelOverride = null)
    {
        if (!forceVisible && !settings.IsExtensionPropertyVisible(field.Node)) return;
        var label = labelOverride ?? settings.ExtensionPropertyLabel(field.Node) ?? field.Label;
        Gui.PushID(field.Key);
        try
        {
            if (forceVisible && ContentAssetReference.Supports(field.Type))
            {
                ReferencePicker.DrawInspector(field.Type, settings.ReadImportSetting(field), field.AllowsNull, updated =>
                {
                    ValidatePendingPropertyStrings(); UseTextureSettingsCommandContext();
                    settings.UpdateImportSetting(field, updated); settings.EndImportSettingEdit();
                }, canEdit: settings.CanEditImportSetting(field));
                DrawAssetSettingResetToBase(settings, field.Node);
                return;
            }
            if (!forceVisible && TryDrawRegisteredImportProperty(settings, field)) return;
            var nativeEntries = field.Collection == null ? settings.ImportSettingEntries(field) : Array.Empty<Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands.AbstractNodeEntry>();
            if (nativeEntries.Count > 0)
            {
                if (BeginDocumentedPropertyRow(label, field.Node, settings.AuthoredAsset.GetType()))
                {
                    var value = settings.ReadImportSetting(field);
                    Gui.BeginDisabled(!settings.CanEditImportSetting(field) || !CanExecuteNativeFactory(settings,
                        new Stride.Core.Quantum.NodeAccessor(field.Node, Stride.Core.Quantum.NodeIndex.Empty), nativeEntries));
                    if (Gui.BeginCombo("##Type", NativeAbstractEntryControl.Label(nativeEntries, value)))
                    {
                        NativeAbstractEntryControl.Choices(nativeEntries, value, entry => SelectNativeFactoryEntry(settings, field.Node, entry,
                            () => settings.ImportSettingEntries(field), () => settings.ChangeImportSettingEntry(field, entry)));
                        Gui.EndCombo();
                    }
                    Gui.EndDisabled(); DrawAssetSettingResetToBase(settings, field.Node);
                    if (CurveBinding.IsSupported(field.Node.Retrieve()))
                    {
                        Gui.SameLine();
                        if (icons!.Button("edit-curve", "Edit curve", 16)) OpenCurveEditor(settings, field.Node, label);
                    }
                    Gui.EndTable();
                }
            }
            if (settings.ReadImportSetting(field) is Stride.Core.Yaml.IUnloadable unknown)
            {
                DrawUnloadableProperty(unknown);
                return;
            }
            if (field.Collection != null && field.ElementType != null)
            { DrawImportAssetCollection(settings, field); return; }
            if (field.Children.Count > 0)
            {
                var open = Gui.TreeNodeEx("##Members", ImGuiTreeNodeFlags.DefaultOpen); Gui.SameLine(); Gui.TextUnformatted(label); PropertyDocumentation.Show(field.Node, settings.AuthoredAsset.GetType());
                DrawAssetSettingResetToBase(settings, field.Node);
                if (nativeEntries.Count == 0 && CurveBinding.IsSupported(field.Node.Retrieve()))
                {
                    Gui.SameLine();
                    if (icons!.Button("edit-curve", "Edit curve", 16)) OpenCurveEditor(settings, field.Node, label);
                }
                if (open)
                {
                    DrawPresentedImportAssetFields(settings, field.Children);
                    Gui.TreePop();
                }
                return;
            }
            if (nativeEntries.Count > 0) return;
            if (!BeginDocumentedPropertyRow(label, field.Node, settings.AuthoredAsset.GetType())) return;
            try
            {
                var canEdit = settings.CanEditImportSetting(field);
                var value = settings.ReadImportSetting(field);
                if (TryDrawMaterialShaderReference(settings, field.Node, label, value,
                    updated => settings.UpdateImportSetting(field, updated), settings.EndImportSettingEdit, false,
                    canEdit: () => settings.CanEditImportSetting(field), controlPath: field.Key)) return;
                if (ContentAssetReference.Supports(field.Type))
                {
                    ReferencePicker.DrawInspector(field.Type, value, field.AllowsNull, updated =>
                    {
                        ValidatePendingPropertyStrings(); UseTextureSettingsCommandContext(); settings.UpdateImportSetting(field, updated); settings.EndImportSettingEdit();
                    }, canEdit: canEdit);
                    DrawAssetSettingResetToBase(settings, field.Node);
                    return;
                }
                if (TryDrawImportSettingDraft(settings, field))
                { DrawAssetSettingResetToBase(settings, field.Node); return; }
                Gui.BeginDisabled(!canEdit);
                try
                {
                    object? updated = value; var changed = false; var deactivated = false;
                    if (field.Type == typeof(bool))
                    { var flag = (bool)(value ?? false); changed = Gui.Checkbox("##Value", ref flag); updated = flag; }
                    else if (field.Type.IsEnum)
                    {
                        var preview = field.EnumOptions.FirstOrDefault(option => Equals(option.Value, value))?.Label ?? value?.ToString() ?? "";
                        if (icons?.HasEnumImages(field.Node.Type) == true)
                            changed = NativeEnumImageControl.Draw(icons, field.Node, field.Node.Type, value, false,
                                field.EnumOptions.Select(option => (Enum)option.Value), UiScale(1), out updated);
                        else if (Gui.BeginCombo("##Value", preview))
                        {
                            var flags = field.Node.Type.IsDefined(typeof(FlagsAttribute), false);
                            var bits = flags ? ImportEnumBits(value!) : 0;
                            foreach (var option in field.EnumOptions)
                            {
                                var mask = flags ? ImportEnumBits(option.Value) : 0;
                                var selected = flags ? mask == 0 ? bits == 0 : (bits & mask) == mask : Equals(option.Value, value);
                                if (Gui.Selectable(option.Label, selected, flags ? ImGuiSelectableFlags.NoAutoClosePopups : ImGuiSelectableFlags.None))
                                { changed = true; updated = flags ? Enum.ToObject(field.Type, mask == 0 ? 0 : selected ? bits & ~mask : bits | mask) : option.Value; }
                            }
                            if (flags && DrawFlagBulkActions() is { } action)
                            { changed = true; updated = ApplyFlagBulkAction((Enum)value!, action); }
                            Gui.EndCombo();
                        }
                    }
                    else Gui.TextUnformatted(value?.ToString() ?? "");
                    if (changed && canEdit) { UseTextureSettingsCommandContext(); Execute(() => { ValidatePendingPropertyStrings(); settings.UpdateImportSetting(field, updated); }); }
                    if (canEdit && (deactivated || Gui.IsItemDeactivated() || changed && (field.Type == typeof(bool) || field.Type.IsEnum))) Execute(settings.EndImportSettingEdit);
                    if (Controls.Enabled && Gui.IsItemVisible()) Controls.Record("asset-settings/" + field.Key);
                    DrawAssetSettingResetToBase(settings, field.Node);
                }
                finally { Gui.EndDisabled(); }
            }
            finally { Gui.EndTable(); }
        }
        finally { Gui.PopID(); }
    }
    private static ulong ImportEnumBits(object value)
    {
        var underlying = Enum.GetUnderlyingType(value.GetType());
        return underlying == typeof(ulong) || underlying == typeof(uint) || underlying == typeof(ushort) || underlying == typeof(byte)
            ? Convert.ToUInt64(value) : unchecked((ulong)Convert.ToInt64(value));
    }
}
