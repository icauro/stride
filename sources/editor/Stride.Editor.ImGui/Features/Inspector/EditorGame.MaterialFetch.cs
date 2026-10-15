using Hexa.NET.ImGui;
using Stride.Rendering;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawMaterialFetchContext(Material? material, bool mixed = false)
    {
        if (Gui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && Gui.IsMouseReleased(ImGuiMouseButton.Right)) Gui.OpenPopup("MaterialReferenceActions");
        if (!Gui.BeginPopup("MaterialReferenceActions")) return;
        var reference = mixed ? null : ContentAssetReference.Read(material);
        var entry = reference == null ? null : workspace?.AssetItems.FirstOrDefault(asset => asset.Id == reference.Id);
        Gui.BeginDisabled(entry == null);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Select the referenced asset")))
        {
            if (assetBrowser.RevealAsset(workspace!, entry!.Id) is { } revealed) selectedAssetPath = revealed.Path;
        }
        Gui.EndDisabled(); Gui.EndPopup();
    }
}
