using Hexa.NET.ImGui;
using Stride.Core.Reflection;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private Action? pendingGameSettingCollectionAction;
    private void DrawGameSettingCollection(AssetMetadataDocument settings, GameSettingField field)
    {
        if (settings.GetGameSettingCollection(field) is { } collection) DrawGameSettingCollection(settings, collection);
        else Gui.TextUnformatted(field.Label);
    }
    private void DrawGameSettingCollection(AssetMetadataDocument settings, GameSettingCollection field)
    {
        Gui.PushID(field.Key);
        var label = field.Parent == null ? settings.PresentedGameSettingLabel(field.Origin) : field.Label;
        var open = Gui.TreeNodeEx(label, ImGuiTreeNodeFlags.DefaultOpen);
        DrawAssetSettingResetContext(settings, field.Parent == null ? field.Origin.Node : field.Node);
        if (open)
        {
            var items = settings.GetGameSettingItems(field);
            foreach (var item in items.Where(settings.IsPresentedGameSettingItemVisible))
            {
                if (!settings.CanCopyGameSettingItem(item)) break;
                Gui.PushID(item.Key);
                if (field.CollectionDescriptor is DictionaryDescriptor)
                {
                    if (!DrawGameItemKeyDraft(settings, item)) { Gui.PopID(); break; }
                }
                else Gui.TextUnformatted(settings.PresentedGameSettingItemLabel(item));
                var itemContextHovered = Gui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
                if (settings.GetNestedGameSettingCollection(item) is { } nested)
                    DrawGameSettingCollection(settings, nested);
                else if (!TryDrawGameItemValueDraft(settings, item))
                    DrawGameSettingItem(settings, item);
                if (!settings.CanCopyGameSettingItem(item)) { Gui.PopID(); break; }
                if ((itemContextHovered || Gui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) && Gui.IsMouseReleased(ImGuiMouseButton.Right))
                    Gui.OpenPopup("##ItemActions");
                if (Gui.BeginPopup("##ItemActions"))
                {
                    try
                    {
                        if (ContentAssetReference.Supports(item.Type)) DrawContentReferenceFetchActions([item.Value]);
                        if (Gui.MenuItem(EditorLocalization.Widget("Copy", "Menu"), "", false, settings.CanCopyGameSettingItem(item))) Execute(() => Gui.SetClipboardText(settings.CopyGameSettingItem(item)));
                        var text = Gui.GetClipboardTextS() ?? ""; var canPaste = settings.CanPasteGameSettingItem(item, text);
                        if (Gui.MenuItem(EditorLocalization.Widget("Paste", "Menu"), "", false, canPaste)) pendingGameSettingCollectionAction = () => settings.PasteGameSettingItem(item, text, false);
                        if (Gui.MenuItem("Replace", "", false, canPaste)) pendingGameSettingCollectionAction = () => settings.PasteGameSettingItem(item, text, true);
                        if (Gui.MenuItem(EditorLocalization.Widget("Reset to base value", "Menu"), "", false, settings.CanEditPresentedGameSettingItem(item) && settings.CanResetAssetSettingItem(item.Collection, item.Index)))
                            pendingGameSettingCollectionAction = () => settings.ResetAssetSettingItem(item.Collection, item.Index);
                        DrawGameItemCommands(settings, item);
                    }
                    finally { Gui.EndPopup(); }
                }
                if (CanExecuteGameItemRemove(settings, item) && Gui.SmallButton(EditorLocalization.Widget("Remove", "Button"))) pendingGameSettingCollectionAction = () => RemoveGameItemThroughCommand(settings, item);
                if (settings.CanReorderGameSettingCollection(field) && settings.CanEditPresentedGameSettingItem(item))
                {
                    Gui.SameLine(); Gui.BeginDisabled(item.Index.Int == 0);
                    if (Gui.SmallButton(EditorLocalization.Widget("Move up", "Menu"))) pendingGameSettingCollectionAction = () => settings.MoveGameSettingItem(item, item.Index.Int - 1);
                    Gui.EndDisabled(); Gui.SameLine(); Gui.BeginDisabled(item.Index.Int == items.Count - 1);
                    if (Gui.SmallButton(EditorLocalization.Widget("Move down", "Menu"))) pendingGameSettingCollectionAction = () => settings.MoveGameSettingItem(item, item.Index.Int + 1);
                    Gui.EndDisabled();
                }
                Gui.PopID();
            }
            DrawGameCollectionAdd(settings, field);
            Gui.TreePop();
        }
        Gui.PopID();
    }
    private void DrawGameSettingItem(AssetMetadataDocument settings, GameSettingItem item)
    {
        var value = item.Value; var type = item.Type;
        if (settings.GetNestedGameSettingCollection(item) is { } nested) { DrawGameSettingCollection(settings, nested); return; }
        var suppliedEntries = settings.ExtensionAbstractEntries(item.Collection, item.Index);
        var entries = suppliedEntries != null || type.IsInterface || type.IsAbstract || value == null ? settings.GameItemEntries(item) : [];
        if (entries.Count != 0)
        {
            using var disabled = ImGuiScopes.Disabled(!settings.CanEditPresentedGameSettingItem(item) || IsAuthoringPipelineUpdating);
            if (Gui.BeginCombo("##Type", NativeAbstractEntryControl.Label(entries, value)))
            {
                try { NativeAbstractEntryControl.Choices(entries, value, entry => pendingGameSettingCollectionAction = () => SelectNativeFactoryEntry(settings,
                    new Stride.Core.Quantum.NodeAccessor(item.Collection, item.Index), entry,
                    () => settings.GameItemEntries(item), () => settings.ChangeGameItemEntry(item, entry)),
                    entry => CanExecuteNativeFactory(settings, new(item.Collection, item.Index), [entry])); }
                finally { Gui.EndCombo(); }
            }
        }
        if (value is Stride.Core.Yaml.IUnloadable unknown) DrawUnloadableProperty(unknown);
        else if (item.Children.Count > 0) DrawGameSettingFields(settings, item.Children);
        else if (value == null || suppliedEntries != null) { }
        else
        {
            var text = value.ToString() ?? "";
            NativeStringInput.Draw("##Value", ref text, ImGuiInputTextFlags.ReadOnly);
        }
        if (!settings.CanCopyGameSettingItem(item)) return;
        var nullEntry = NativeAbstractEntries.TypeEntry(settings.GameItemEntries(item), null);
        var accessor = new Stride.Core.Quantum.NodeAccessor(item.Collection, item.Index);
        if (value != null && nullEntry != null && CanExecuteNativeFactory(settings, accessor, [nullEntry]) && Gui.SmallButton("Clear"))
            pendingGameSettingCollectionAction = () => SelectNativeFactoryEntry(settings, accessor, nullEntry,
                () => settings.GameItemEntries(item), () => settings.ChangeGameItemEntry(item, nullEntry));
    }
}
