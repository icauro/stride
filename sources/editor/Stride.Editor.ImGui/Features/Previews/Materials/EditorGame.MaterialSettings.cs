using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawMaterialSettings(AssetMetadataDocument document)
    {
        if (document.MaterialSettings == null) return;
        document.MaterialShaderSources = () => CaptureMaterialShaderSources(document);
        Gui.PushID("MaterialSettings");
        DrawPresentedMaterialRootFields(document);
        DrawMaterialLayers(document);
        Gui.PopID();
    }
    private void DrawPresentedMaterialFields(AssetMetadataDocument document, IReadOnlyList<MaterialSettingField> fields)
        => DrawPresentedAssetRows(document, fields.Select(field => new PresentedAssetRow(field.Node, field.Category,
            document.MaterialSettingOrder(field), document.MaterialSettingExpansion(field), () => DrawMaterialSetting(document, field))));
    private void DrawMaterialLayers(AssetMetadataDocument document)
    {
        if (!Gui.CollapsingHeader("Layers", ImGuiTreeNodeFlags.DefaultOpen)) return;
        Action? action = null;
        DrawNativeMaterialLayerAdd(document, command => action = command);
        var layers = document.MaterialSettingLayers;
        foreach (var presented in PresentedMaterialLayers(document, layers))
        {
            var index = presented.Index; var layer = presented.Layer;
            Gui.PushID(layer.Id.ToString());
            var origin = Gui.GetCursorScreenPos();
            ApplyNativeItemExpansion(presented.Target?.Presenter);
            var open = Gui.TreeNodeEx("##LayerTree", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth);
            Gui.GetWindowDrawList().AddText(origin + new System.Numerics.Vector2(Gui.GetTreeNodeToLabelSpacing(), 0),
                Gui.GetColorU32(ImGuiCol.Text), document.MaterialLayerLabel(layer, index));
            var rename = false;
            if (Gui.BeginPopupContextItem("##LayerActions"))
            {
                if (Gui.MenuItem(EditorLocalization.Widget("Copy", "Menu"), "", false, document.CanCopyMaterialLayer(layer))) Execute(() => { ValidatePendingPropertyStrings(); Gui.SetClipboardText(document.CopyMaterialLayer(layer)); });
                var clipboard = Gui.GetClipboardTextS() ?? "";
                var canPaste = document.CanPasteMaterialLayer(layer, clipboard);
                if (Gui.MenuItem(EditorLocalization.Widget("Paste", "Menu"), "", false, canPaste)) action = () => document.PasteMaterialLayer(layer, clipboard, false);
                if (Gui.MenuItem("Replace", "", false, canPaste)) action = () => document.PasteMaterialLayer(layer, clipboard, true);
                rename = Gui.MenuItem(EditorLocalization.Widget("Rename", "Menu"), "", false, layer.Fields.Any(field => field.Node.Name == nameof(Stride.Rendering.Materials.MaterialBlendLayer.Name)
                    && field.CanAssign && document.CanEditAssetSettingNode(field.Node) && !document.IsMaterialSettingReadOnly(field)));
                if (Gui.MenuItem(EditorLocalization.Widget("Remove", "Button"), "", false, document.CanRemoveMaterialLayer(layer))) action = () => RemoveNativeMaterialLayer(document, layer);
                if (Gui.MenuItem(EditorLocalization.Widget("Move up", "Menu"), "", false, index > 0 && document.CanEditMaterialLayer(layer))) { var target = index - 1; action = () => document.MoveMaterialLayer(layer, target); }
                if (Gui.MenuItem(EditorLocalization.Widget("Move down", "Menu"), "", false, index + 1 < layers.Count && document.CanEditMaterialLayer(layer))) { var target = index + 1; action = () => document.MoveMaterialLayer(layer, target); }
                if (document.CanResetMaterialLayer(layer) && Gui.MenuItem(EditorLocalization.Widget("Reset to base value", "Menu"))) action = () => document.ResetMaterialLayer(layer);
                DrawMaterialLayerCommands(document, layer);
                Gui.EndPopup();
            }
            if (rename) Gui.OpenPopup("##RenameLayer");
            if (Gui.BeginPopup("##RenameLayer"))
            {
                var name = layer.Fields.FirstOrDefault(field => field.Node.Name == nameof(Stride.Rendering.Materials.MaterialBlendLayer.Name));
                if (name != null) DrawMaterialSetting(document, name, true);
                Gui.EndPopup();
            }
            var layerEnabled = layer.Fields.FirstOrDefault(field => field.Node.Name == "Enabled" && field.Type == typeof(bool));
            if (layerEnabled != null) { Gui.SameLine(); DrawMaterialSetting(document, layerEnabled, true, "##Enabled"); }
            var layerMaterial = document.InlinedMaterialLayerSetting(layer);
            if (layerMaterial != null) DrawMaterialSetting(document, layerMaterial, true, "##Material");
            if (open)
            {
                DrawPresentedMaterialLayerFields(document, layer, layer.Fields.Where(field => field.Node.Name != nameof(Stride.Rendering.Materials.MaterialBlendLayer.Name)
                    && !ReferenceEquals(field, layerEnabled) && !ReferenceEquals(field, layerMaterial)).ToArray());
                Gui.TreePop();
            }
            Gui.PopID();
        }
        if (action != null) { UseTextureSettingsCommandContext(); Execute(() => { ValidatePendingPropertyStrings(); action(); }); }
    }
    private void DrawMaterialSetting(AssetMetadataDocument document, MaterialSettingField field, bool forceVisible = false, string? labelOverride = null)
    {
        if (!forceVisible && !document.IsMaterialSettingVisible(field)) return;
        var label = labelOverride ?? document.MaterialSettingLabel(field);
        var entries = document.MaterialSettingEntries(field);
        var hasEntries = entries.Count > 0 && field.CanAssign && CanExecuteNativeFactory(document,
            new Stride.Core.Quantum.NodeAccessor(field.Node, Stride.Core.Quantum.NodeIndex.Empty), entries);
        var canEdit = document.CanEditMaterialSettings && document.CanEditAssetSettingNode(field.Node) && !document.IsMaterialSettingReadOnly(field);
        var nativeReference = ContentAssetReference.Supports(field.Type) && document.SessionHistory?.Extensions?.HasPrimitiveEditor(field.Type) != true;
        Gui.PushID(field.Key);
        Gui.BeginDisabled(!canEdit && field.Children.Count == 0 && field.CollectionField == null && !nativeReference);
        var value = document.ReadMaterialSetting(field);
        if (TryDrawRegisteredMaterialProperty(document, field, label))
        { Gui.EndDisabled(); DrawAssetSettingResetToBase(document, field.Node); Gui.PopID(); return; }
        if (field.CollectionField is { } collection)
        {
            DrawImportAssetCollection(document, collection);
            Gui.EndDisabled(); Gui.PopID(); return;
        }
        if (hasEntries && BeginInspectorPropertyRow(label))
        {
            Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands.AbstractNodeEntry? selected = null;
            Gui.BeginDisabled(!canEdit);
            if (BeginNativeCombo("##Type", NativeAbstractEntryControl.Label(entries, value)))
            {
                for (var index = 0; index < entries.Count; index++)
                {
                    var entry = entries[index]; Gui.PushID(index);
                    if (entry is Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands.AbstractNodeTypeGroup group)
                    {
                        if (Gui.BeginMenu(group.DisplayValue))
                        {
                            var ordinal = 0;
                            foreach (var candidate in group.Types.OrderBy(candidate => candidate.Order).ThenBy(candidate => candidate.DisplayValue, StringComparer.CurrentCulture))
                            {
                                Gui.PushID(ordinal++);
                                if (Gui.Selectable(candidate.DisplayValue, candidate.IsMatchingValue(value!))) selected = candidate;
                                Gui.PopID();
                            }
                            Gui.EndMenu();
                        }
                    }
                    else if (Gui.Selectable(entry.DisplayValue, entry.IsMatchingValue(value!))) selected = entry;
                    Gui.PopID();
                }
                Gui.EndCombo();
            }
            Gui.EndDisabled();
            Gui.EndTable();
            if (selected != null)
            {
                UseTextureSettingsCommandContext(); Execute(() => ReplaceMaterialSettingEntry(document, field, selected));
                Gui.EndDisabled(); Gui.PopID(); return;
            }
        }
        if (value is Stride.Core.Yaml.IUnloadable unknown)
        {
            DrawUnloadableProperty(unknown);
            Gui.EndDisabled(); DrawAssetSettingResetToBase(document, field.Node); Gui.PopID(); return;
        }
        if (hasEntries && CurveBinding.IsSupported(field.Node.Retrieve()))
        {
            Gui.SameLine();
            if (icons!.Button("edit-curve", "Edit curve", 16)) OpenCurveEditor(document, field.Node, label);
        }
        if (field.Children.Count > 0)
        {
            var inline = document.InlinedMaterialSetting(field);
            var enabled = field.Children.FirstOrDefault(child => child.Node.Name == "Enabled" && child.Type == typeof(bool));
            if (enabled != null) { if (hasEntries) Gui.SameLine(); DrawMaterialSetting(document, enabled, true, "##Enabled"); }
            if (inline != null) DrawMaterialSetting(document, inline, true, "##InlineValue");
            var expansion = document.MaterialSettingExpansion(field);
            if (expansion == Stride.Core.ExpandRule.Always) Gui.SetNextItemOpen(true);
            else if (expansion == Stride.Core.ExpandRule.Never) Gui.SetNextItemOpen(false);
            var open = Gui.TreeNodeEx(hasEntries ? "##Properties" : label, expansion == Stride.Core.ExpandRule.Never ? ImGuiTreeNodeFlags.None : ImGuiTreeNodeFlags.DefaultOpen);
            DrawAssetSettingResetToBase(document, field.Node);
            if (!hasEntries && CurveBinding.IsSupported(field.Node.Retrieve()))
            {
                Gui.SameLine();
                if (icons!.Button("edit-curve", "Edit curve", 16)) OpenCurveEditor(document, field.Node, label);
            }
            if (open)
            {
                DrawPresentedMaterialFields(document, field.Children.Where(child => !ReferenceEquals(child, inline) && !ReferenceEquals(child, enabled)).ToArray());
                Gui.TreePop();
            }
            Gui.EndDisabled(); Gui.PopID(); return;
        }
        if (IsMaterialShaderReference(document, field.Node))
        {
            Gui.EndDisabled();
            try
            {
                TryDrawMaterialShaderReference(document, field.Node, label, value,
                    updated => document.UpdateMaterialSetting(field, updated), document.EndMaterialSettingEdit,
                    drawLabel: !label.StartsWith("##", StringComparison.Ordinal),
                    canEdit: () => CanEditMaterialSettingDraft(document, field), controlPath: field.Key);
            }
            finally { Gui.PopID(); }
            return;
        }
        Gui.BeginDisabled((!field.CanAssign || !canEdit) && !nativeReference);
        object? updated = null; var changed = false;
        if (ContentAssetReference.Supports(field.Type))
        {
            if (!label.StartsWith("##", StringComparison.Ordinal)) Gui.TextUnformatted(label);
            DrawContentReferenceValue(field.Type, value, field.AllowsNull, reference =>
            {
                UseTextureSettingsCommandContext(); document.UpdateMaterialSetting(field, reference); document.EndMaterialSettingEdit();
            }, canEdit: field.CanAssign && canEdit);
            Gui.EndDisabled(); Gui.EndDisabled(); DrawAssetSettingResetToBase(document, field.Node); Gui.PopID(); return;
        }
        if (IsPackageColorType(field.Type))
        {
            Gui.EndDisabled(); Gui.EndDisabled();
            try { DrawMaterialColorDraft(document, field, label); }
            finally { Gui.PopID(); }
            return;
        }
        if (SupportsMaterialSpecialDraft(field.Type) || field.Type == typeof(string) || AssetSettingNumericMetadata.Supports(field.Type)
            || field.Type == typeof(Vector2) || field.Type == typeof(Vector3) || field.Type == typeof(Vector4))
        {
            Gui.EndDisabled(); Gui.EndDisabled();
            try { TryDrawMaterialSettingDraft(document, field, label); DrawAssetSettingResetToBase(document, field.Node); }
            finally { Gui.PopID(); }
            return;
        }
        if (field.Type == typeof(bool))
        {
            var item = value is true;
            Gui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, value == null);
            try { changed = Gui.Checkbox(label, ref item); updated = item; }
            finally { Gui.PopItemFlag(); }
        }
        else if (field.Type.IsEnum)
        {
            var flags = field.Node.Type.IsDefined(typeof(FlagsAttribute), false);
            var bits = flags ? ImportEnumBits(value ?? Enum.ToObject(field.Type, 0)) : 0;
            if (icons?.HasEnumImages(field.Node.Type) == true)
            {
                if (!label.StartsWith("##", StringComparison.Ordinal)) { Gui.TextUnformatted(label); Gui.SameLine(); }
                changed = NativeEnumImageControl.Draw(icons, field.Node, field.Node.Type, value, false,
                    field.EnumOptions.Select(option => (Enum)option.Value), UiScale(1), out updated);
            }
            else if (Gui.BeginCombo(label, field.EnumOptions.FirstOrDefault(option => Equals(option.Value, value))?.Label ?? value?.ToString()))
            {
                foreach (var option in field.EnumOptions)
                {
                    Gui.PushID(option.Name);
                    var mask = flags ? ImportEnumBits(option.Value) : 0;
                    var selected = flags ? mask == 0 ? bits == 0 : (bits & mask) == mask : Equals(option.Value, value);
                    if (Gui.Selectable(option.Label, selected, flags ? ImGuiSelectableFlags.NoAutoClosePopups : ImGuiSelectableFlags.None))
                    {
                        updated = flags ? EnumFromBits(field.Type, mask == 0 ? 0 : selected ? bits & ~mask : bits | mask) : option.Value;
                        changed = true;
                    }
                    Gui.PopID();
                }
                if (flags && DrawFlagBulkActions() is { } operation)
                { updated = ApplyFlagBulkAction((Enum)(value ?? Enum.ToObject(field.Type, 0)), operation); changed = true; }
                Gui.EndCombo();
            }
        }
        else if (!hasEntries && BeginInspectorPropertyRow(label))
        {
            try
            {
                var text = value == null ? EditorLocalization.Text("(None)") : MaterialSettingField.Name(value.GetType());
                DrawSelectableText("##TypeValue", text, wrap: true, size: new System.Numerics.Vector2(Gui.GetContentRegionAvail().X, Gui.GetTextLineHeightWithSpacing()));
            }
            finally { Gui.EndTable(); }
        }
        if (changed) { UseTextureSettingsCommandContext(); Execute(() => { ValidatePendingPropertyStrings(); document.UpdateMaterialSetting(field, updated); }); }
        if (Gui.IsItemDeactivated() || changed && (field.Type == typeof(bool) || field.Type.IsEnum)) Execute(document.EndMaterialSettingEdit);
        Gui.EndDisabled(); Gui.EndDisabled(); DrawAssetSettingResetToBase(document, field.Node); Gui.PopID();
    }
}
