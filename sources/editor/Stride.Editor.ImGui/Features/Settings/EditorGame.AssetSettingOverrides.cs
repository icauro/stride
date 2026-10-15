using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawAssetSettingResetContext(AssetMetadataDocument settings, IGraphNode node)
    {
        if (!Gui.BeginPopupContextItem("ResetAssetSetting")) return;
        if (node is IMemberNode member) DrawPropertyClipboardCommands(settings, member);
        DrawRegisteredPropertyCommands(settings, node, settings.CanEditAssetSettingNode(node), settings.ApplyInheritedAssetMutation);
        if (Gui.MenuItem(EditorLocalization.Widget("Reset to base value", "Menu"), "", false, settings.CanResetAssetSettingNode(node)))
            Execute(() =>
            {
                if (!IsCurrentMetadataControlOwner(settings) || !settings.CanResetAssetSettingNode(node))
                    throw new InvalidOperationException("The property reset target changed.");
                ValidatePendingPropertyStrings();
                if (!IsCurrentMetadataControlOwner(settings) || !settings.CanResetAssetSettingNode(node))
                    throw new InvalidOperationException("The property reset target changed during pending validation.");
                settings.ResetAssetSettingNode(node); RemoveGameSettingDrafts(settings);
            });
        Gui.EndPopup();
    }
    private void DrawAssetSettingLabel(AssetMetadataDocument settings, IGraphNode node, string label)
    {
        var inherited = node is IAssetMemberNode { BaseNode: not null };
        var overridden = node is IAssetMemberNode member && member.IsContentOverridden();
        var bold = PushPackagePropertiesBoldFont(overridden);
        if (inherited && !overridden)
        {
            var color = Gui.GetStyle().Colors[(int)Hexa.NET.ImGui.ImGuiCol.Text]; color.W *= 0.5f;
            Gui.PushStyleColor(Hexa.NET.ImGui.ImGuiCol.Text, color);
        }
        Gui.PushTextWrapPos(Gui.GetCursorPosX() + Math.Max(1, Gui.GetContentRegionAvail().X));
        try { DrawNativeLabel(label); }
        finally { Gui.PopTextWrapPos(); }
        if (inherited && !overridden) Gui.PopStyleColor(); if (bold) Gui.PopFont();
        DrawAssetSettingResetContext(settings, node);
    }
}
