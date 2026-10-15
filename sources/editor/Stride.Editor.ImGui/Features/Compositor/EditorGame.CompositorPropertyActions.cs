using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawCompositorPropertyContext(AssetMetadataDocument document, CompositorProperty property)
    {
        HandleCompositorClipboardKeys(document, [property]);
        if (property.Accessor.IsItem) return; // Its owning collection supplies the item action popup.
        if (!Gui.BeginPopupContextItem("##Property actions")) return;
        DrawCompositorClipboardActions(document, [property]);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Reset to base value", "Menu"), string.Empty, false, property.Accessor.IsItem
            ? property.Accessor.Node is Stride.Core.Assets.Quantum.IAssetObjectNode collection
                && document.CanEditCompositorProperty(property) && document.CanResetAssetSettingItem(collection, property.Accessor.Index)
            : document.CanResetAssetSettingNode(property.Accessor.Node)))
        {
            UseTextureSettingsCommandContext();
            ExecuteCompositorCommand(document, () =>
            {
                if (property.Accessor.IsItem) document.ResetAssetSettingItem((Stride.Core.Assets.Quantum.IAssetObjectNode)property.Accessor.Node, property.Accessor.Index);
                else document.ResetAssetSettingNode(property.Accessor.Node);
            });
        }
        if (property.NativePresenter != null) DrawCompositorItemPresenterCommands(document, [property]);
        Gui.EndPopup();
    }
    private void DrawCompositorClipboardActions(AssetMetadataDocument document, IReadOnlyList<CompositorProperty> fields)
    {
        if (fields.All(field => ContentAssetReference.Supports(field.Type)))
        {
            var references = fields.Select(field => ContentAssetReference.Read(field.Accessor.RetrieveValue())).Where(reference => reference != null)
                .Select(reference => reference!.Id).Distinct().ToArray();
            if (DrawNativeMenuItem(EditorLocalization.Widget("Select the referenced asset"), string.Empty, false, workspace != null && references.Any(id => workspace.AssetItems.Any(asset => asset.Id == id))))
                ExecuteCompositorCommand(document, () =>
                {
                    foreach (var id in references)
                        if (assetBrowser.RevealAsset(workspace!, id) is { } revealed) selectedAssetPath = revealed.Path;
                }, requiresEdit: false);
        }
        if (DrawNativeMenuItem(EditorLocalization.Widget("Copy", "Menu"), "Ctrl+C", false, fields.Count == 1 && document.CanCopyCompositorProperty(fields[0])))
            ExecuteCompositorCommand(document, () => { var text = document.CopyCompositorProperty(fields[0]); if (!string.IsNullOrEmpty(text)) Gui.SetClipboardText(text); }, requiresEdit: false);
        var clipboard = Gui.GetClipboardTextS() ?? "";
        var canPaste = document.CanPasteCompositorProperties(fields, clipboard);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Paste", "Menu"), "Ctrl+V", false, canPaste)) Apply(false);
        if (DrawNativeMenuItem("Replace", "Ctrl+Shift+V", false, canPaste)) Apply(true);
        void Apply(bool replace)
        { UseTextureSettingsCommandContext(); ExecuteCompositorCommand(document, () => document.PasteCompositorProperties(fields, clipboard, replace)); }
    }
    private void HandleCompositorClipboardKeys(AssetMetadataDocument document, IReadOnlyList<CompositorProperty> fields)
    {
        var io = Gui.GetIO();
        if (!Gui.IsItemFocused() || !io.KeyCtrl || io.KeyAlt || io.KeySuper) return;
        // Ordinary text selection retains ImGui's native copy/paste. WPF's explicit
        // Ctrl+Shift+V routed Replace command targets the property even in a textbox.
        if (!io.WantTextInput && !Gui.IsItemActive() && Gui.IsKeyPressed(ImGuiKey.C) && fields.Count == 1 && document.CanCopyCompositorProperty(fields[0]))
            ExecuteCompositorCommand(document, () => { var text = document.CopyCompositorProperty(fields[0]); if (!string.IsNullOrEmpty(text)) Gui.SetClipboardText(text); }, requiresEdit: false);
        else if ((!io.WantTextInput && !Gui.IsItemActive() || io.KeyShift) && Gui.IsKeyPressed(ImGuiKey.V))
        {
            var text = Gui.GetClipboardTextS() ?? "";
            if (!document.CanPasteCompositorProperties(fields, text)) return;
            var replace = io.KeyShift;
            UseTextureSettingsCommandContext(); ExecuteCompositorCommand(document, () => document.PasteCompositorProperties(fields, text, replace));
        }
    }
}
