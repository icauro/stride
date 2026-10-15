using Hexa.NET.ImGui;
using Stride.Assets.SpriteFont;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private string installedFontFilter = "";
    private static readonly Lazy<string[]> InstalledFontNames = new(() =>
    {
        using var fonts = new System.Drawing.Text.InstalledFontCollection();
        return fonts.Families.Select(family => family.Name).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
    });
    private void DrawFontSettings(AssetMetadataDocument document)
    {
        if (!document.IsFontAsset) return;
        DrawPresentedFontFields(document, document.FontSettingsFields);
    }
    private void DrawPresentedFontFields(AssetMetadataDocument document, IReadOnlyList<FontSettingField> fields)
        => DrawPresentedAssetRows(document, fields.Select(field => new PresentedAssetRow(field.Node, field.Category,
            field.Node.MemberDescriptor.Order, Stride.Core.ExpandRule.Always, () => DrawFontSetting(document, field))));
    private void DrawFontSetting(AssetMetadataDocument document, FontSettingField member)
    {
        if (!document.IsExtensionPropertyVisible(member.Node)) return;
        var value = document.ReadFontSetting(member);
        Gui.PushID(member.Key);
        var label = document.ExtensionPropertyLabel(member.Node) ?? member.Label;
        try
        {
            if (value is not Stride.Core.Yaml.IUnloadable && TryDrawRegisteredFontSetting(document, member, label)) return;
        }
        finally { Gui.PopID(); }
        Gui.PushID(member.Key);
        IReadOnlyList<Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands.AbstractNodeEntry> entries
            = member.Collection == null ? document.FontSettingEntries(member) : [];
        var installedFontName = document.AuthoredAsset is SpriteFontAsset
            && member.Node.Name == nameof(SystemFontProvider.FontName) && member.Type == typeof(string);
        if (entries.Count > 0)
        {
            var selected = false;
            Gui.BeginDisabled(!document.CanEditFontSetting(member));
            if (Gui.BeginCombo(label, NativeAbstractEntryControl.Label(entries, value)))
            {
                NativeAbstractEntryControl.Choices(entries, value, entry =>
                {
                    selected = true;
                    UseTextureSettingsCommandContext();
                    SelectNativeFactoryEntry(document, member.Node, entry,
                        () => document.FontSettingEntries(member), () => document.ChangeFontSettingEntry(member, entry));
                });
                Gui.EndCombo();
            }
            Gui.EndDisabled(); DrawAssetSettingResetToBase(document, member.Node);
            if (selected) { Gui.PopID(); return; }
        }
        if (value is Stride.Core.Yaml.IUnloadable unknown)
        {
            if (entries.Count == 0) DrawAssetSettingLabel(document, member.Node, label);
            DrawUnloadableProperty(unknown);
            DrawAssetSettingResetToBase(document, member.Node);
            Gui.PopID(); return;
        }
        if (member.Collection is { } collection)
        {
            var open = Gui.TreeNodeEx("##Items", ImGuiTreeNodeFlags.DefaultOpen); Gui.SameLine(); Gui.TextUnformatted(label);
            if (open)
            {
                Action? action = null;
                DrawFontCollectionAdd(document, member, command => action = command);
                foreach (var row in document.PresentedFontItems(member))
                {
                    var item = row.Item;
                    Gui.PushID(item.Id == Stride.Core.Reflection.ItemId.Empty ? item.Index.ToString() : item.Id.ToString());
                    try
                    {
                    if (row.Expansion != Stride.Core.ExpandRule.Auto)
                        Gui.SetNextItemOpen(row.Expansion is Stride.Core.ExpandRule.Always or Stride.Core.ExpandRule.Once, ImGuiCond.Once);
                    var expanded = Gui.TreeNodeEx("##Item", ImGuiTreeNodeFlags.DefaultOpen);
                    var headerHovered = Gui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
                    Gui.SameLine(); Gui.TextUnformatted(row.Label);
                    headerHovered |= Gui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
                    Gui.SameLine(); Gui.BeginDisabled(!document.CanRemoveFontItem(member, item));
                    if (Gui.SmallButton("×")) action = () => { ValidatePendingPropertyStrings(); document.RemoveFontSettingItem(member, item); }; Gui.EndDisabled();
                    DrawFontItemContext(document, member, item, headerHovered, command => action = () => { ValidatePendingPropertyStrings(); command(); });
                    if (expanded)
                    {
                        try
                        {
                            if (item.Value is Stride.Core.Yaml.IUnloadable unloaded) DrawUnloadableProperty(unloaded);
                            else if (!TryDrawRegisteredFontItem(document, member, item)) DrawFontItemValue(document, member, item);
                        }
                        finally { Gui.TreePop(); }
                    }
                    }
                    finally { Gui.PopID(); }
                }
                if (action != null) { UseTextureSettingsCommandContext(); Execute(action); }
                Gui.TreePop();
            }
        }
        else if (member.Children.Count > 0)
        {
            var open = Gui.TreeNodeEx("##Members", ImGuiTreeNodeFlags.DefaultOpen); Gui.SameLine(); Gui.TextUnformatted(label);
            if (open) { DrawPresentedFontFields(document, member.Children); Gui.TreePop(); }
        }
        else if (entries.Count == 0)
        {
            if (ContentAssetReference.Supports(member.Type))
            {
                Gui.TextUnformatted(label);
                DrawContentReferenceValue(member.Type, value, member.AllowsNull,
                    selected => CommitFontSettingDraft(document, member, selected),
                    canEdit: CanEditRetainedFontSetting(document, member));
                DrawAssetSettingResetToBase(document, member.Node); Gui.PopID(); return;
            }
            if (!installedFontName && DrawRetainedFontSetting(document, member, label))
            {
                DrawAssetSettingResetToBase(document, member.Node); Gui.PopID(); return;
            }
            Gui.BeginDisabled(!document.CanEditFontSetting(member));
            object? updated = null; var changed = false;
            if (installedFontName)
            {
                if (Gui.BeginCombo(label, value?.ToString()))
                {
                    NativeStringInput.Draw("##Filter", ref installedFontFilter);
                    foreach (var name in InstalledFontNames.Value.Where(name => name.Contains(installedFontFilter, StringComparison.CurrentCultureIgnoreCase)))
                        if (Gui.Selectable(name, Equals(value, name))) { updated = name; changed = true; }
                    Gui.EndCombo();
                }
            }
            else if (member.Type == typeof(bool)) { var item = (bool)value!; changed = Gui.Checkbox(label, ref item); updated = item; }
            else if (member.Type.IsEnum)
            {
                if (Gui.BeginCombo(label, value?.ToString()))
                {
                    foreach (var name in member.EnumNames)
                    {
                        var option = Enum.Parse(member.Type, name); var flags = member.Type.IsDefined(typeof(FlagsAttribute), false);
                        var mask = Convert.ToInt64(option); var current = Convert.ToInt64(value);
                        if (Gui.Selectable(name, flags ? mask == 0 ? current == 0 : (current & mask) == mask : Equals(option, value), flags ? ImGuiSelectableFlags.NoAutoClosePopups : ImGuiSelectableFlags.None))
                        { updated = flags ? Enum.ToObject(member.Type, mask == 0 ? 0 : current ^ mask) : option; changed = true; }
                    }
                    Gui.EndCombo();
                }
            }
            else DrawSelectableText("##FontValue", label + ": " + value);
            var cancel = document.CanEditFontSetting(member) && (Gui.IsItemActive() || Gui.IsItemDeactivated()) && Gui.IsKeyPressed(ImGuiKey.Escape);
            if (cancel) Execute(document.CancelFontSettingEdit);
            else
            {
                if (changed && updated != null) { UseTextureSettingsCommandContext(); Execute(() => { ValidatePendingPropertyStrings(); document.UpdateFontSetting(member, updated); }); }
                if (Gui.IsItemDeactivated() || changed && (member.Type == typeof(bool) || member.Type.IsEnum || installedFontName)) Execute(document.EndFontSettingEdit);
            }
            Gui.EndDisabled(); DrawAssetSettingResetToBase(document, member.Node);
        }
        Gui.PopID();
    }
}
