using Hexa.NET.ImGui;
using Stride.Assets.Sprite;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawSpriteCanvasToolbar(AssetMetadataDocument document, SpriteSettingFrame frame,
        SpriteCanvasState state, TextureAssetPreview? preview)
    {
        var dpi = Gui.GetStyle().FontScaleDpi;
        var buttonSize = new NVector2(22 * dpi);
        var first = true;
        Gui.PushStyleVar(ImGuiStyleVar.FramePadding, new NVector2(3 * dpi));
        void Next()
        {
            var right = Gui.GetCursorScreenPos().X + Gui.GetContentRegionAvail().X;
            if (!first && Gui.GetItemRectMax().X + Gui.GetStyle().ItemSpacing.X + buttonSize.X <= right) Gui.SameLine();
            first = false;
        }
        bool Icon(string image, string tooltip, bool selected = false)
        {
            Next();
            if (selected) Gui.PushStyleColor(ImGuiCol.Button, Gui.GetStyle().Colors[(int)ImGuiCol.HeaderActive]);
            var clicked = icons.Button(image, EditorLocalization.Text(tooltip, "ToolTip"), 16);
            if (selected) Gui.PopStyleColor();
            return clicked;
        }
        void Tool(string image, string tooltip, SpriteCanvasTool tool)
        {
            if (Icon(image, tooltip, state.Tool == tool)) state.Tool = state.Tool == tool ? SpriteCanvasTool.None : tool;
        }
        if (Icon("selection-previous", "Select previous sprite")) Execute(() => document.SelectNextSpriteFrame(-1));
        if (Icon("selection-next", "Select next sprite")) Execute(() => document.SelectNextSpriteFrame(1));
        if (Icon("zoom-out", "Zoom out")) ZoomSpriteCanvas(state, preview, false);
        if (Icon("zoom-in", "Zoom in")) ZoomSpriteCanvas(state, preview, true);
        if (Icon("actual-size", "Scale to real pixel size")) ChangeSpriteCanvasScale(state, preview, 1);
        if (Icon("fit", "Fit image to screen") && preview != null) ChangeSpriteCanvasScale(state, preview,
            Math.Max(.001f, Math.Min((state.ScrollSize.X / dpi - 20) / preview.Width, (state.ScrollSize.Y / dpi - 20) / preview.Height)));
        if (Icon("sprite-focus-region", "Center view on current sprite region")) FocusSelectedSpriteRegion(document);
        Next();
        if (Gui.Button("##SpriteRectangleColor", buttonSize)) Gui.OpenPopup("SpriteRectangleColor");
        var minimum = Gui.GetItemRectMin() + new NVector2(3 * dpi);
        Gui.GetWindowDrawList().AddRect(minimum, minimum + new NVector2(16 * dpi), Gui.GetColorU32(state.BorderColor), 0, ImDrawFlags.None, dpi);
        if (Gui.IsItemHovered()) Gui.SetTooltip(EditorLocalization.Text("Change selection rectangle color", "ToolTip"));
        if (Gui.BeginPopup("SpriteRectangleColor"))
        {
            Gui.ColorPicker4("##Value", ref state.BorderColor);
            Gui.EndPopup();
        }
        if (Icon("sprite-highlight-selection", "Highlight the current selection", state.Highlight)) state.Highlight = !state.Highlight;
        Gui.BeginDisabled(!document.CanEditSpriteSettings);
        if (Icon("sprite-select-all", "Select entire image"))
        { UseTextureSettingsCommandContext(); Execute(() => QueueWholeSpriteImages(document)); }
        Tool("sprite-magic-wand", "Select the sprite region using the magic wand", SpriteCanvasTool.MagicWand);
        Next();
        if (Gui.ArrowButton("##MagicWandOptions", ImGuiDir.Down)) Gui.OpenPopup("MagicWandOptions");
        if (Gui.BeginPopup("MagicWandOptions"))
        {
            if (Gui.RadioButton(EditorLocalization.Widget("Use transparency", "Button"), state.MagicWandTransparency)) state.MagicWandTransparency = true;
            if (Gui.RadioButton(EditorLocalization.Widget("Use color key", "Button"), !state.MagicWandTransparency)) state.MagicWandTransparency = false;
            Gui.EndPopup();
        }
        var tool = document.SpriteSettings!.Type == SpriteSheetType.Sprite2D ? SpriteCanvasTool.SpriteCenter : SpriteCanvasTool.Borders;
        Next();
        var selected = state.Tool == tool;
        if (selected) Gui.PushStyleColor(ImGuiCol.Button, Gui.GetStyle().Colors[(int)ImGuiCol.HeaderActive]);
        if (Gui.Button("##SpriteGeometricTool", buttonSize)) state.Tool = selected ? SpriteCanvasTool.None : tool;
        if (selected) Gui.PopStyleColor();
        var toolOrigin = Gui.GetItemRectMin() + new NVector2(3 * dpi);
        DrawSpriteGeometricTool(toolOrigin, 16 * dpi, Gui.GetColorU32(state.BorderColor), tool);
        if (Gui.IsItemHovered()) Gui.SetTooltip(EditorLocalization.Text(tool == SpriteCanvasTool.SpriteCenter
            ? "Move the sprite center" : "Resize the sprite borders", "ToolTip"));
        if (document.SpriteSettings.Type == SpriteSheetType.UI)
        {
            var locked = document.IsSpriteBordersLocked(frame.Id);
            if (Icon(locked ? "sprite-locked" : "sprite-unlocked", locked ? "Unlock the sprite borders" : "Lock the sprite borders", locked))
                document.SetSpriteBordersLocked(frame.Id, !locked);
        }
        Gui.EndDisabled();
        Gui.PopStyleVar();
    }

    private static void DrawSpriteGeometricTool(NVector2 origin, float size, uint color, SpriteCanvasTool tool)
    {
        var draw = Gui.GetWindowDrawList();
        if (tool == SpriteCanvasTool.Borders)
        {
            var dash = size / 8;
            for (var i = 0; i < 8; i += 2)
            {
                var from = i * dash; var to = (i + 1) * dash;
                draw.AddLine(origin + new NVector2(from, 0), origin + new NVector2(to, 0), color);
                draw.AddLine(origin + new NVector2(from, size), origin + new NVector2(to, size), color);
                draw.AddLine(origin + new NVector2(0, from), origin + new NVector2(0, to), color);
                draw.AddLine(origin + new NVector2(size, from), origin + new NVector2(size, to), color);
            }
            return;
        }
        // Original SpriteEditorView center Path spans [3,73] in both axes.
        NVector2 Point(float x, float y) => origin + new NVector2(x - 3, y - 3) * (size / 70);
        draw.AddRectFilled(Point(20, 36), Point(56, 40), color);
        draw.AddRectFilled(Point(36, 20), Point(40, 56), color);
        ReadOnlySpan<float> starts = [3, 10, 15, 58, 63, 68];
        foreach (var start in starts)
        {
            var length = start is 3 or 68 ? 5 : 3;
            draw.AddRectFilled(Point(start, 37), Point(start + length, 39), color);
            draw.AddRectFilled(Point(37, start), Point(39, start + length), color);
        }
    }
}
