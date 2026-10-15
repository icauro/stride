using Hexa.NET.ImGui;
using Stride.Assets.Sprite;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawSpriteCanvasOverlay(AssetMetadataDocument document, SpriteSettingFrame frame, SpriteCanvasState state,
        NVector2 origin, NVector2 size, NVector2 imageOrigin, float scale, int imageWidth, int imageHeight, bool hovered)
    {
        var io = Gui.GetIO(); var draw = Gui.GetWindowDrawList(); var end = origin + size;
        NVector2 Screen(float x, float y) => imageOrigin + new NVector2(x, y) * scale;
        var pointer = (io.MousePos - imageOrigin) / scale;
        var region = frame.Value.TextureRegion;
        var topLeft = Screen(region.X, region.Y); var bottomRight = Screen(region.X + region.Width, region.Y + region.Height);
        var hit = SpriteCanvasDrag.None;
        var borderColor = Gui.ColorConvertFloat4ToU32(state.BorderColor);
        draw.PushClipRect(origin, end, true);
        if (state.Highlight && imageWidth > 0 && imageHeight > 0)
        {
            var imageEnd = Screen(imageWidth, imageHeight);
            var shadeColor = Gui.GetStyle().Colors[(int)ImGuiCol.Border]; shadeColor.W = .75f;
            var shade = Gui.GetColorU32(shadeColor);
            draw.AddRectFilled(imageOrigin, new(imageEnd.X, topLeft.Y), shade);
            draw.AddRectFilled(new(imageOrigin.X, bottomRight.Y), imageEnd, shade);
            draw.AddRectFilled(new(imageOrigin.X, topLeft.Y), new(topLeft.X, bottomRight.Y), shade);
            draw.AddRectFilled(new(bottomRight.X, topLeft.Y), new(imageEnd.X, bottomRight.Y), shade);
        }
        var dpi = Gui.GetStyle().FontScaleDpi;
        draw.AddRect(topLeft - new NVector2(dpi), bottomRight + new NVector2(dpi), borderColor, 0, ImDrawFlags.None, 2 * dpi);
        if (state.Tool == SpriteCanvasTool.None)
            hit = HitSpriteCanvasHandles(io.MousePos, topLeft - new NVector2(6 * dpi), bottomRight + new NVector2(6 * dpi), dpi, false);
        if (document.SpriteSettings!.Type == SpriteSheetType.Sprite2D)
        {
            var pivot = frame.Value.Center + (frame.Value.CenterFromMiddle ? new Vector2(region.Width, region.Height) / 2 : new Vector2(0.5f));
            var pivotScreen = Screen(region.X + pivot.X, region.Y + pivot.Y);
            draw.AddRectFilled(pivotScreen - new NVector2(8, 1.263f) * dpi, pivotScreen + new NVector2(8, 1.263f) * dpi, borderColor);
            draw.AddRectFilled(pivotScreen - new NVector2(1.263f, 8) * dpi, pivotScreen + new NVector2(1.263f, 8) * dpi, borderColor);
            if (state.Tool == SpriteCanvasTool.SpriteCenter && Math.Abs(pivotScreen.X - io.MousePos.X) <= 8 * dpi
                && Math.Abs(pivotScreen.Y - io.MousePos.Y) <= 8 * dpi) hit = SpriteCanvasDrag.Pivot;
        }
        else if (document.SpriteSettings.Type == SpriteSheetType.UI)
        {
            var borders = frame.Value.Borders;
            var borderMinimum = Screen(region.X + borders.X, region.Y + borders.Y) - new NVector2(6 * dpi);
            var borderMaximum = Screen(region.X + region.Width - borders.Z, region.Y + region.Height - borders.W) + new NVector2(6 * dpi);
            DrawSpriteBorderRectangle(borderMinimum + new NVector2(5 * dpi), borderMaximum - new NVector2(5 * dpi), borderColor, dpi, origin, end);
            if (state.Tool == SpriteCanvasTool.Borders) hit = HitSpriteCanvasHandles(io.MousePos, borderMinimum, borderMaximum, dpi, true);
        }
        draw.PopClipRect();
        if (hovered || state.Drag != SpriteCanvasDrag.None) SetSpriteCanvasDragCursor(state.Drag != SpriteCanvasDrag.None ? state.Drag : hit);
        if (hovered && imageWidth > 0 && imageHeight > 0 && state.Tool is SpriteCanvasTool.ColorPick or SpriteCanvasTool.MagicWand)
            WindowsMouseCursor.RequestArtwork(state.Tool == SpriteCanvasTool.ColorPick ? "sprite-color-picker" : "sprite-magic-wand");
        if (hovered && Gui.IsMouseClicked(ImGuiMouseButton.Left) && CanMutateSpriteCanvasPresentation(document, frame, state) && document.CanEditSpriteSettings)
        {
            if (state.Tool is SpriteCanvasTool.ColorPick or SpriteCanvasTool.MagicWand && pointer.X >= 0 && pointer.Y >= 0
                && pointer.X < imageWidth && pointer.Y < imageHeight)
                QueueSpritePixelTool(document, frame, state, new Int2((int)pointer.X, (int)pointer.Y), io.KeyCtrl);
            var name = hit == SpriteCanvasDrag.Pivot ? nameof(SpriteInfo.Center)
                : (int)hit >= (int)SpriteCanvasDrag.BorderLeft ? nameof(SpriteInfo.Borders) : nameof(SpriteInfo.TextureRegion);
            if (hit != SpriteCanvasDrag.None && frame.Fields.FirstOrDefault(candidate => candidate.Node.Name == name) is { } field && document.CanEditSpriteSetting(field))
            {
                UseTextureSettingsCommandContext();
                if (Services.Commands.TryExecute(() =>
                {
                    if (!CanMutateSpriteCanvasPresentation(document, frame, state)) throw new InvalidOperationException("The sprite presentation is no longer active.");
                    ValidatePendingPropertyStrings();
                    if (!CanMutateSpriteCanvasPresentation(document, frame, state) || !document.CanEditSpriteSetting(field))
                        throw new InvalidOperationException("The sprite owner changed while finishing pending edits.");
                    document.BeginSpriteSettingEdit(field, SpriteCanvasEditTitle(hit));
                }))
                {
                    state.Drag = hit; state.DragField = field;
                    state.DragLocalPoint = (io.MousePos - SpriteCanvasThumbOrigin(frame, imageOrigin, scale, dpi, hit)) / dpi;
                    state.LastDragScreenPoint = io.MousePos;
                }
            }
        }
        if (state.Drag != SpriteCanvasDrag.None)
        {
            if (Gui.IsKeyPressed(ImGuiKey.Escape)) { Execute(document.CancelSpriteSettingEdit); state.Drag = SpriteCanvasDrag.None; }
            else if (!Gui.IsMouseDown(ImGuiMouseButton.Left)) { Execute(document.EndSpriteSettingEdit); state.Drag = SpriteCanvasDrag.None; }
            else if (CanMutateSpriteCanvasPresentation(document, frame, state) && state.LastDragScreenPoint != io.MousePos)
            {
                state.LastDragScreenPoint = io.MousePos;
                UpdateSpriteCanvasDrag(document, frame, state, io.MousePos, imageOrigin, scale, dpi, imageWidth, imageHeight);
            }
        }
    }
}
