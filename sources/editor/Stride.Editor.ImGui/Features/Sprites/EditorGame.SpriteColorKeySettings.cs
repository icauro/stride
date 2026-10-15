using Hexa.NET.ImGui;
using Stride.Assets.Sprite;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawSpriteColorKeySettings(AssetMetadataDocument document)
    {
        var field = document.SpriteSettingsFields.First(member => member.Node.Name == nameof(SpriteSheetAsset.ColorKeyEnabled));
        var enabled = document.SpriteSettings!.ColorKeyEnabled;
        Gui.BeginDisabled(!CanEditSpriteSettingDraft(document, field));
        if (Gui.Checkbox(EditorLocalization.Widget("Use color key", "Button"), ref enabled))
        { UseTextureSettingsCommandContext(); Execute(() => { ValidatePendingPropertyStrings(); CommitSpriteSettingDraft(document, field, enabled); }); }
        if (Gui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) Gui.SetTooltip(EditorLocalization.Text(
            "If enabled, the sprite uses the given color as transparency", "ToolTip"));
        Gui.EndDisabled();
        var dpi = Gui.GetStyle().FontScaleDpi;
        if (!enabled) { Gui.Dummy(new NVector2(1, 22 * dpi)); return; }
        var colorField = document.SpriteSettingsFields.First(member => member.Node.Name == nameof(SpriteSheetAsset.ColorKeyColor));
        if (!document.IsExtensionPropertyVisible(colorField.Node)) return;
        Gui.Indent(21 * dpi);
        if (!Gui.BeginTable("##ColorKeyRow", 2, ImGuiTableFlags.SizingStretchProp)) { Gui.Unindent(21 * dpi); return; }
        try
        {
        Gui.TableSetupColumn("##Color", ImGuiTableColumnFlags.WidthStretch);
        Gui.TableSetupColumn("##Pixel", ImGuiTableColumnFlags.WidthFixed, 24 * dpi);
        Gui.TableNextColumn();
        DrawSpriteColorDraft(document, colorField);
        Gui.TableNextColumn();
        var state = GetSpriteCanvasState(document); var selected = state.Tool == SpriteCanvasTool.ColorPick;
        Gui.BeginDisabled(document.SelectedSpriteFrame == null || !CanEditSpriteSettingDraft(document, colorField));
        if (selected) Gui.PushStyleColor(ImGuiCol.Button, Gui.GetStyle().Colors[(int)ImGuiCol.HeaderActive]);
        if (icons.Button("sprite-color-picker", EditorLocalization.Text("To select the color key,  select a pixel color from the image", "ToolTip"), 16))
            Execute(() => { ValidatePendingPropertyStrings(); if (!CanEditSpriteSettingDraft(document, colorField)) throw new InvalidOperationException("The sprite color owner changed."); state.Tool = selected ? SpriteCanvasTool.None : SpriteCanvasTool.ColorPick; });
        if (selected) Gui.PopStyleColor();
        Gui.EndDisabled();
        }
        finally { Gui.EndTable(); Gui.Unindent(21 * dpi); }
    }
}
