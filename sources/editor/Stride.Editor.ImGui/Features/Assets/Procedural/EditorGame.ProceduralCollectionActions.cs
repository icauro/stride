using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawProceduralCollectionItemActions(ProceduralAssetDocument asset, ProceduralCollectionItem item)
    {
        void Run(Action action) => Execute(() =>
        {
            if (!ReferenceEquals(assetDocument, asset)) throw new InvalidOperationException("The procedural selection changed.");
            ValidatePendingPropertyStrings();
            if (!ReferenceEquals(assetDocument, asset) || !asset.IsCollectionItemCurrent(item))
                throw new InvalidOperationException("The procedural collection item changed.");
            action();
        });
        if (Gui.BeginPopupContextItem("Item property actions"))
        {
            try
            {
                DrawProceduralItemPresenterCommands(asset, item);
                if (DrawNativeMenuItem(EditorLocalization.Widget("Copy", "Menu"), "", false, asset.IsCollectionItemCurrent(item)))
                    Run(() => { Gui.SetClipboardText(asset.CopyCollectionItem(item)); });
                var clipboard = Gui.GetClipboardTextS() ?? "";
                if (DrawNativeMenuItem(EditorLocalization.Widget("Paste", "Menu"), "", false, asset.CanPasteCollectionItem(item, clipboard)))
                    Run(() => { asset.PasteCollectionItem(item, clipboard, false); });
                if (DrawNativeMenuItem("Replace", "", false, asset.CanPasteCollectionItem(item, clipboard)))
                    Run(() => { asset.PasteCollectionItem(item, clipboard, true); });
                if (DrawNativeMenuItem(EditorLocalization.Widget("Remove", "Button"), "", false, asset.CanRemoveCollectionItem(item)))
                    Run(() => { asset.RemoveCollectionItem(item); });
                if (DrawNativeMenuItem(EditorLocalization.Widget("Move up", "Menu"), "", false, asset.CanMoveCollectionItem(item) && item.Index.Int > 0))
                    Run(() => { asset.MoveCollectionItem(item, item.Index.Int - 1); });
                if (DrawNativeMenuItem(EditorLocalization.Widget("Move down", "Menu"), "", false, asset.CanMoveCollectionItem(item) && item.Index.Int + 1 < (item.Context.Node.Indices ?? []).Count()))
                    Run(() => { asset.MoveCollectionItem(item, item.Index.Int + 1); });
                if (DrawNativeMenuItem(EditorLocalization.Widget("Reset to base value", "Menu"), "", false, asset.CanResetCollectionItem(item)))
                    Run(() => { asset.ResetCollectionItem(item); });
            }
            finally { Gui.EndPopup(); }
        }

    }
}
