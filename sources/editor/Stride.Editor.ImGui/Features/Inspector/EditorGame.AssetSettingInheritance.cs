using Stride.Core.Quantum;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawAssetSettingResetToBase(AssetMetadataDocument document, IGraphNode node)
    {
        if (Gui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && Gui.IsMouseReleased(ImGuiMouseButton.Right))
            Gui.OpenPopup("##ResetToBase");
        if (!Gui.BeginPopup("##ResetToBase")) return;
        try
        {
            if (node is IMemberNode property) DrawPropertyClipboardCommands(document, property);
            if (document.CanResetAssetSettingNode(node) && DrawNativeMenuItem(EditorLocalization.Widget("Reset to base value", "Menu"))) Execute(() => document.ResetAssetSettingNode(node));
            DrawRegisteredPropertyCommands(document, node, document.CanEditAssetSettingNode(node), document.ApplyInheritedAssetMutation);
        }
        finally { Gui.EndPopup(); }
    }
}
