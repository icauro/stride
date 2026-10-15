using Hexa.NET.ImGui;
using Stride.Core;
using Stride.Core.IO;
using Stride.Core.Mathematics;
using Stride.Core.Reflection;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void UseTextureSettingsCommandContext()
    {
        assetRelocationCommandContext = assetTagBatchCommandContext = packageFolderCommandContext = false;
    }
    private void DrawTextureSettings(AssetMetadataDocument document)
    {
        if (document.TextureSettings == null) return;
        Gui.PushID("TextureSettings");
        var rows = document.TextureSettingsFields.Select(field => new PresentedAssetRow(field.Node, field.Category,
            field.Node.MemberDescriptor.Order, TextureCategoryMetadata(field)?.Expand ?? ExpandRule.Always,
            () => DrawTextureSetting(document, field), TextureCategoryMetadata(field)?.Order)).ToList();
        if (document.TextureTypeSettingNode is { } typeNode)
            rows.Add(new(typeNode, "Format", typeNode.MemberDescriptor.Order, ExpandRule.Always, () => DrawTextureTypeSetting(document), 20));
        DrawPresentedAssetRows(document, rows);
        Gui.PopID();
    }
    private static Stride.Core.Annotations.CategoryOrderAttribute? TextureCategoryMetadata(TextureSettingField field)
        => Stride.Core.Reflection.TypeDescriptorFactory.Default.AttributeRegistry.GetAttributes<Stride.Core.Annotations.CategoryOrderAttribute>(field.Node.MemberDescriptor.DeclaringType)
            .FirstOrDefault(category => category.Name == field.Category);
    private void DrawTextureTypeSetting(AssetMetadataDocument document)
    {
        if (document.TextureSettings is not { } settings || document.TextureTypeSettingNode is not { } node) return;
        var type = settings.Type;
        if (TryDrawRegisteredTextureType(document, node)) { DrawAssetSettingResetToBase(document, node); return; }
        var entries = document.TextureTypeEntries();
        Gui.BeginDisabled(IsAuthoringPipelineUpdating || !document.CanChangeTextureTypeEntry());
        try
        {
            if (Gui.BeginCombo(document.ExtensionPropertyLabel(node) ?? "Type", NativeAbstractEntryControl.Label(entries, type)))
            {
                try
                {
                    NativeAbstractEntryControl.Choices(entries, type, entry =>
                    {
                        if (!document.CanChangeTextureTypeEntry()) return;
                        UseTextureSettingsCommandContext();
                        SelectNativeFactoryEntry(document, node, entry, document.TextureTypeEntries,
                            () => document.ChangeTextureTypeEntry(entry));
                    });
                }
                finally { Gui.EndCombo(); }
            }
        }
        finally { Gui.EndDisabled(); }
        DrawAssetSettingResetToBase(document, node);
    }
    private void DrawTextureSetting(AssetMetadataDocument document, TextureSettingField field)
    {
        if (!document.IsExtensionPropertyVisible(field.Node)) return;
        Gui.PushID(field.Key);
        var label = document.ExtensionPropertyLabel(field.Node) ?? field.Label;
        try
        {
            if (TryDrawRegisteredTextureSetting(document, field, label))
            { DrawAssetSettingResetToBase(document, field.Node); return; }
        }
        finally { Gui.PopID(); }
        Gui.PushID(field.Key);
        if (field.Type == typeof(float) || field.Type == typeof(UFile) || field.Type == typeof(Color))
        {
            try
            {
                if (field.Type == typeof(float)) DrawTextureNumberDraft(document, field, label);
                else if (field.Type == typeof(UFile)) DrawTexturePathDraft(document, field, label);
                else DrawTextureColorDraft(document, field, label);
                if (field.Type == typeof(float) && Controls.Enabled) Controls.Record("texture-settings/" + field.Key);
                if (field.Type != typeof(Color)) DrawAssetSettingResetToBase(document, field.Node);
            }
            finally { Gui.PopID(); }
            return;
        }
        var editable = document.CanEditTextureSettings && document.CanEditAssetSettingNode(field.Node)
            && !document.IsExtensionPropertyReadOnly(field.Node);
        Gui.BeginDisabled(!editable);
        var value = field.Node.Retrieve(); object? updated = null; var changed = false;
        if (field.Type == typeof(bool))
        {
            var flag = (bool)value!; changed = Gui.Checkbox(label, ref flag); updated = flag;
        }
        else if (field.Type.IsEnum)
        {
            if (icons!.HasEnumImages(field.Type))
            {
                Gui.TextUnformatted(label); Gui.SameLine();
                changed = NativeEnumImageControl.Draw(icons, field.Node, field.Type, value, false,
                    Enum.GetValues(field.Type).Cast<Enum>(), UiScale(1), out updated);
            }
            else
            {
                var current = Array.IndexOf(field.EnumNames, value!.ToString());
                if (Gui.BeginCombo(label, current >= 0 ? field.EnumLabels[current] : value.ToString()))
                {
                    for (var index = 0; index < field.EnumNames.Length; index++)
                        if (Gui.Selectable(field.EnumLabels[index], current == index)) { updated = Enum.Parse(field.Type, field.EnumNames[index]); changed = true; }
                    Gui.EndCombo();
                }
            }
        }
        else { Gui.TextUnformatted(label); }
        if (Controls.Enabled) Controls.Record("texture-settings/" + field.Key);
        if (editable && Gui.IsItemActivated()) { UseTextureSettingsCommandContext(); Execute(() => { ValidatePendingPropertyStrings(); document.BeginTextureSettingEdit(field); }); }
        if (editable && changed && updated != null) { UseTextureSettingsCommandContext(); Execute(() => { ValidatePendingPropertyStrings(); document.UpdateTextureSetting(field, updated); }); }
        if (editable && (Gui.IsItemDeactivated() || changed && (field.Type == typeof(bool) || field.Type.IsEnum)))
            Execute(document.EndTextureSettingEdit);
        Gui.EndDisabled(); DrawAssetSettingResetToBase(document, field.Node); Gui.PopID();
    }
}
