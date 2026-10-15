using Hexa.NET.ImGui;
using Stride.Assets.Models;
using Stride.Navigation.Assets;
using Stride.Core;
using Stride.Core.Reflection;
using Stride.Core.IO;
using Stride.Core.Mathematics;
using System.Globalization;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawImportAssetCollection(AssetMetadataDocument settings, AssetImportSettingField field)
    {
        var open = Gui.TreeNodeEx("##Collection", ImGuiTreeNodeFlags.DefaultOpen); Gui.SameLine();
        Gui.TextUnformatted(field.IndexedCollection == null ? settings.ExtensionPropertyLabel(field.Node) ?? field.Label : field.Label);
        if (field.IndexedCollection == null) DrawAssetSettingResetToBase(settings, field.Node);
        Action? action = null;
        if (settings.HasSkeletonPreservationActions(field))
        {
            Gui.SameLine(); Gui.BeginDisabled(!settings.CanSetSkeletonPreservation(field));
            if (Gui.SmallButton(EditorLocalization.Widget("All", "Button"))) action = () => settings.SetSkeletonPreservation(field, true);
            Gui.SameLine();
            if (Gui.SmallButton(EditorLocalization.Widget("None", "Button"))) action = () => settings.SetSkeletonPreservation(field, false);
            Gui.EndDisabled();
        }
        if (!open)
        {
            if (action != null) { UseTextureSettingsCommandContext(); Execute(action); }
            return;
        }
        try
        {
            foreach (var presented in PresentedImportItems(settings, field))
            {
                var item = presented.Item;
                Gui.PushID(item.Key);
                try
                {
                    // WPF SkeletonAssetNodeUpdater presents only the preserved
                    // checkbox and actual imported node name/depth, not editable names.
                    if (item.Value is NodeInformation node && item.Children.FirstOrDefault(child => child.Node.Name == nameof(NodeInformation.Preserve)) is { } preserve)
                    {
                        var selected = (bool)(settings.ReadImportSetting(preserve) ?? true);
                        Gui.BeginDisabled(!settings.CanEditImportItem(item) || !settings.CanEditImportSetting(preserve));
                        if (Gui.Checkbox(new string(' ', Math.Clamp(node.Depth, 0, 128) * 2) + node.Name, ref selected))
                        { var value = selected; action = () => { settings.UpdateImportSetting(preserve, value); settings.EndImportSettingEdit(); }; }
                        Gui.EndDisabled();
                        DrawSkeletonItemContext(settings, item, command => action = command);
                        continue;
                    }
                    var name = item.Value is ModelMaterial material ? string.IsNullOrWhiteSpace(material.Name) ? "Material " + item.Index : material.Name : item.Index.ToString();
                    if (item.Value is not ModelMaterial) name = settings.ExtensionItemLabel(item.Collection, item.Index) ?? name;
                    ApplyNativeItemExpansion(presented.Target?.Presenter);
                    var expanded = Gui.TreeNodeEx("##Item", ImGuiTreeNodeFlags.DefaultOpen);
                    var itemContextHovered = Gui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
                    Gui.SameLine(); Gui.TextUnformatted(name);
                    itemContextHovered |= Gui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
                    if (settings.CanRemoveImportCollection(field))
                    {
                        Gui.SameLine(); Gui.BeginDisabled(!settings.CanEditImportItem(item));
                        if (Gui.SmallButton("×")) action = () => RemoveNativeImportCollectionItem(settings, item);
                        Gui.EndDisabled();
                    }
                    if (settings.CanReorderImportCollection(field))
                    {
                        Gui.SameLine(); Gui.BeginDisabled(!settings.CanEditImportItem(item) || item.Index.Int <= 0);
                        if (Gui.SmallButton(EditorLocalization.Widget("Move up", "Menu"))) action = () => settings.MoveImportCollectionItem(item, item.Index.Int - 1);
                        Gui.EndDisabled(); Gui.SameLine(); Gui.BeginDisabled(!settings.CanEditImportItem(item) || item.Index.Int >= field.Items.Count - 1);
                        if (Gui.SmallButton(EditorLocalization.Widget("Move down", "Menu"))) action = () => settings.MoveImportCollectionItem(item, item.Index.Int + 1);
                        Gui.EndDisabled();
                    }
                    if ((itemContextHovered || Gui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) && Gui.IsMouseReleased(ImGuiMouseButton.Right))
                        Gui.OpenPopup("##ResetItem");
                    if (Gui.BeginPopup("##ResetItem"))
                    {
                        if (ContentAssetReference.Supports(item.Type)) DrawContentReferenceFetchActions([item.Value]);
                        if (DrawNativeMenuItem(EditorLocalization.Widget("Copy", "Menu"), "", false, settings.CanCopyImportItem(item))) Execute(() => { ValidatePendingPropertyStrings(); Gui.SetClipboardText(settings.CopyImportItem(item)); });
                        var text = Gui.GetClipboardTextS() ?? ""; var canPaste = settings.CanPasteImportItem(item, text);
                        if (DrawNativeMenuItem(EditorLocalization.Widget("Paste", "Menu"), "", false, canPaste)) action = () => settings.PasteImportItem(item, text, false);
                        if (DrawNativeMenuItem("Replace", "", false, canPaste)) action = () => settings.PasteImportItem(item, text, true);
                        if (DrawNativeMenuItem(EditorLocalization.Widget("Reset to base value", "Menu"), "", false, settings.CanResetAssetSettingItem(item.Collection, item.Index))) action = () => settings.ResetAssetSettingItem(item.Collection, item.Index);
                        DrawImportItemCommands(settings, item);
                        Gui.EndPopup();
                    }
                    if (expanded)
                    {
                        if (settings.CanRenameImportCollectionKey(item))
                        {
                            DrawImportDictionaryKey(settings, item);
                        }
                        if (item.Value is not Stride.Core.Yaml.IUnloadable && TryDrawRegisteredImportItem(settings, item))
                        {
                            Gui.TreePop();
                            continue;
                        }
                        var suppliedEntries = settings.ExtensionAbstractEntries(item.Collection, item.Index);
                        if (suppliedEntries != null)
                        {
                            var entries = settings.ImportItemEntries(item);
                            Gui.BeginDisabled(!settings.CanEditImportItem(item) || !CanExecuteNativeFactory(settings,
                                new Stride.Core.Quantum.NodeAccessor(item.Collection, item.Index), entries));
                            if (entries.Count != 0 && Gui.BeginCombo("##Type", NativeAbstractEntryControl.Label(entries, item.Value)))
                            {
                                NativeAbstractEntryControl.Choices(entries, item.Value, entry => action = () => SelectNativeFactoryEntry(settings,
                                    new Stride.Core.Quantum.NodeAccessor(item.Collection, item.Index), entry,
                                    () => settings.ImportItemEntries(item), () => settings.ChangeImportItemEntry(item, entry)));
                                Gui.EndCombo();
                            }
                            Gui.EndDisabled();
                        }
                        if (item.Value is Stride.Core.Yaml.IUnloadable unknown)
                        {
                            DrawUnloadableProperty(unknown);
                            Gui.TreePop();
                            continue;
                        }
                        if (item.NestedCollection is { } nested) DrawImportAssetCollection(settings, nested);
                        else if (item.Value is ModelMaterial && item.Children.FirstOrDefault(child => child.Node.Name == nameof(ModelMaterial.MaterialInstance)) is { } instance)
                        {
                            // The WPF inline presenter hides Material from ordinary child enumeration.
                            // Bind that exact authored node explicitly, retaining the shared picker/history.
                            var reference = instance.Children.FirstOrDefault(child => child.Node.Name == nameof(Stride.Rendering.MaterialInstance.Material));
                            if (reference != null) DrawImportAssetSetting(settings, reference, forceVisible: true, labelOverride: name);
                            DrawPresentedImportItemFields(settings, item, instance.Children.Where(child => !ReferenceEquals(child, reference)).ToArray());
                        }
                        else if (item.Children.Count > 0) DrawPresentedImportItemFields(settings, item, item.Children);
                        else
                        {
                            DrawImportAssetCollectionValue(settings, item);
                            if (item.Value != null && !item.Type.IsValueType && item.Type != typeof(string)
                                && !ContentAssetReference.Supports(item.Type)) DrawPresentedImportItemFields(settings, item, []);
                        }
                        Gui.TreePop();
                    }
                }
                finally { Gui.PopID(); }
            }
            if (field.Collection!.Descriptor is DictionaryDescriptor) DrawImportDictionaryAdd(settings, field);
            else if (settings.CanAddImportCollection(field) && settings.ExtensionCollectionAbstractEntries(field.Collection) == null)
            {
                Gui.BeginDisabled(!AssetCollectionCommandRules.CanAdd(field.ElementType!));
                if (Gui.Button("Add")) action = () => AddDefaultImportCollectionItem(settings, field);
                Gui.EndDisabled();
            }
            else if (settings.CanAddImportCollection(field))
            {
                Gui.SetNextItemWidth(UiScale(18));
                if (Gui.BeginCombo("##AddItem", "Add", ImGuiComboFlags.NoPreview))
                {
                    NativeAbstractEntryControl.Choices(settings.ImportCollectionEntries(field), null,
                        entry => action = () => SelectNativeFactoryEntry(settings,
                            new Stride.Core.Quantum.NodeAccessor(field.Collection, Stride.Core.Quantum.NodeIndex.Empty), entry,
                            () => settings.ImportCollectionEntries(field), () => settings.AddImportCollectionEntry(field, entry, null), "AddNewItem"));
                    Gui.EndCombo();
                }
            }
        }
        finally { Gui.TreePop(); }
        if (action != null) { UseTextureSettingsCommandContext(); Execute(() => { ValidatePendingPropertyStrings(); action(); }); }
    }
    private void DrawImportAssetCollectionValue(AssetMetadataDocument settings, AssetImportSettingItem item)
    {
        var value = settings.ReadImportItem(item); var canEdit = settings.CanEditImportItem(item);
        if (TryDrawRegisteredImportItem(settings, item)) return;
        if (Nullable.GetUnderlyingType(item.Field.ElementType!) is { } underlying)
        {
            var present = value != null;
            Gui.BeginDisabled(!canEdit);
            var changed = DrawNativeNullablePresence(present, out var create, item.AllowsNull); Gui.EndDisabled();
            if (changed)
            { UseTextureSettingsCommandContext(); Execute(() => settings.ChangeImportCollectionItemType(item, create ? underlying : null)); return; }
            if (!present) return;
            Gui.SameLine();
        }
        if (settings.AuthoredAsset is NavigationMeshAsset && item.Field.Node.Name == nameof(NavigationMeshAsset.SelectedGroups))
        {
            DrawNavigationGroupValue(value, false, canEdit, selected => Execute(() =>
            { ValidatePendingPropertyStrings(); UseTextureSettingsCommandContext(); settings.UpdateImportItem(item, selected); settings.EndImportSettingEdit(); }));
            return;
        }
        if (ContentAssetReference.Supports(item.Type))
        {
            ReferencePicker.DrawInspector(item.Type, value, item.AllowsNull, updated =>
            { ValidatePendingPropertyStrings(); UseTextureSettingsCommandContext(); settings.UpdateImportItem(item, updated); settings.EndImportSettingEdit(); }, canEdit: canEdit);
            return;
        }
        if (TryDrawImportItemDraft(settings, item)) return;
        Gui.BeginDisabled(!canEdit);
        try
        {
            var changed = false; var deactivated = false; object? updated = value;
            if (item.Type == typeof(bool)) { var flag = (bool)(value ?? false); changed = Gui.Checkbox("##Value", ref flag); updated = flag; }
            else if (item.Type.IsEnum)
            {
                var names = Enum.GetNames(item.Type);
                var flags = item.Field.ElementType!.IsDefined(typeof(FlagsAttribute), false); var bits = flags ? ImportEnumBits(value!) : 0;
                if (icons?.HasEnumImages(item.Field.ElementType!) == true)
                    changed = NativeEnumImageControl.Draw(icons, item.Collection, item.Field.ElementType!, value, false,
                        names.Select(name => (Enum)Enum.Parse(item.Type, name)), UiScale(1), out updated);
                else if (Gui.BeginCombo("##Value", value?.ToString() ?? ""))
                {
                    foreach (var name in names)
                    {
                        var option = Enum.Parse(item.Type, name); var mask = flags ? ImportEnumBits(option) : 0;
                        var selected = flags ? mask == 0 ? bits == 0 : (bits & mask) == mask : Equals(option, value);
                        var display = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>(item.Type.GetField(name)!);
                        if (Gui.Selectable(display?.Name ?? Stride.Core.Presentation.Core.Utils.SplitCamelCase(name), selected,
                            flags ? ImGuiSelectableFlags.NoAutoClosePopups : ImGuiSelectableFlags.None))
                        { changed = true; updated = flags ? Enum.ToObject(item.Type, mask == 0 ? 0 : selected ? bits & ~mask : bits | mask) : option; }
                    }
                    if (flags && DrawFlagBulkActions() is { } action)
                    { changed = true; updated = ApplyFlagBulkAction((Enum)value!, action); }
                    Gui.EndCombo();
                }
            }
            else Gui.TextUnformatted(value?.ToString() ?? "");
            if (changed) { UseTextureSettingsCommandContext(); Execute(() => { ValidatePendingPropertyStrings(); settings.UpdateImportItem(item, updated); }); }
            if (deactivated || Gui.IsItemDeactivated() || changed && (item.Type == typeof(bool) || item.Type.IsEnum)) Execute(settings.EndImportSettingEdit);
        }
        finally { Gui.EndDisabled(); }
    }
}
