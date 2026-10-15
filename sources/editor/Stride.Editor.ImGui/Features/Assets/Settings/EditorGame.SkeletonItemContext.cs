using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawSkeletonItemContext(AssetMetadataDocument owner, AssetImportSettingItem item, Action<Action> schedule)
    {
        if (Gui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && Gui.IsMouseReleased(ImGuiMouseButton.Right))
            Gui.OpenPopup("##SkeletonItemActions");
        if (!Gui.BeginPopup("##SkeletonItemActions")) return;
        try
        {
            if (DrawNativeMenuItem(EditorLocalization.Widget("Copy", "Menu"), "", false, owner.CanCopyImportItem(item)))
                Execute(() => Gui.SetClipboardText(owner.CopyImportItem(item)));
            var text = Gui.GetClipboardTextS() ?? "";
            // Nodes is structurally read-only. Its actual native clipboard
            // eligibility rejects Paste/Replace, independently of Preserve.
            var canPaste = owner.CanPasteImportItem(item, text);
            if (DrawNativeMenuItem(EditorLocalization.Widget("Paste", "Menu"), "", false, canPaste)) schedule(() => owner.PasteImportItem(item, text, false));
            if (DrawNativeMenuItem("Replace", "", false, canPaste)) schedule(() => owner.PasteImportItem(item, text, true));
            if (DrawNativeMenuItem(EditorLocalization.Widget("Reset to base value", "Menu"), "", false, owner.CanResetAssetSettingItem(item.Collection, item.Index)))
                schedule(() => owner.ResetAssetSettingItem(item.Collection, item.Index));
            DrawImportItemCommands(owner, item);
        }
        finally { Gui.EndPopup(); }
    }
}
