using Hexa.NET.ImGui;
using Stride.Assets.Sprite;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawSpriteCanvasContent(AssetMetadataDocument document)
    {
        var frame = document.SelectedSpriteFrame;
        if (frame == null)
        {
            DrawSpriteCanvasSelectionPrompt();
            return;
        }
        var state = GetSpriteCanvas(document, frame);
        var preview = state.Preview;
        SynchronizeSpriteCanvasDpi(state, preview);
        PumpSpritePixelTool(document, frame, state);
        DrawSpriteCanvasToolbar(document, frame, state, preview);
        using var viewport = ImGuiScopes.Child("Sprite viewport", Gui.GetContentRegionAvail(), ImGuiChildFlags.None,
            ImGuiWindowFlags.HorizontalScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!viewport.DrawContents) return;
        if (preview == null)
        {
            // No bitmap/checkerboard is fabricated when the native source cache
            // cannot supply an image. The toolbar and authored helpers remain.
            {
                state.ScrollSize = NVector2.Max(NVector2.One, Gui.GetContentRegionAvail());
                var emptyOrigin = Gui.GetCursorScreenPos() + new NVector2(Gui.GetScrollX(), Gui.GetScrollY());
                if (state.Load == null) state.ViewScroll = NVector2.Zero;
                state.RestoreViewScroll = true;
                Gui.SetScrollX(0); Gui.SetScrollY(0); Gui.SetCursorScreenPos(emptyOrigin);
                Gui.InvisibleButton("##SpriteCanvas", state.ScrollSize,
                    ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle | ImGuiButtonFlags.MouseButtonRight);
                if (Gui.IsItemHovered() && Gui.GetIO().KeyCtrl && Gui.GetIO().MouseWheel != 0)
                    ZoomSpriteCanvas(state, null, Gui.GetIO().MouseWheel > 0, Gui.GetIO().MousePos - emptyOrigin);
                DrawSpriteCanvasOverlay(document, frame, state, emptyOrigin, state.ScrollSize,
                    emptyOrigin + new NVector2(10 * state.ViewDpi), state.ViewScale * state.ViewDpi, 0, 0, Gui.IsItemHovered());
            }
            return;
        }
        var available = Gui.GetContentRegionAvail(); var size = new NVector2(Math.Clamp(available.X, 1, 4096), Math.Clamp(available.Y, 1, 4096));
        try { preview.Resize((int)size.X, (int)size.Y); }
        catch (Exception error)
        {
            EditorGraphicsFailures.ThrowIfDeviceLost(GraphicsDevice, error);
            if (!ReferenceEquals(state.Error, error)) outputLog.Append("Project", Stride.Core.Diagnostics.LogMessageType.Error,
                "Sprite source", error.Message, error.ToString(), assetId: document.AuthoredAsset.Id);
            state.Error = error; return;
        }
        preview.Visible = true;
        var origin = Gui.GetCursorScreenPos() + new NVector2(Gui.GetScrollX(), Gui.GetScrollY());
        ApplySpriteCanvasScroll(state, preview, size);
        Gui.SetCursorScreenPos(origin);
        var end = origin + size; var draw = Gui.GetWindowDrawList();
        draw.AddImage(preview.TextureRef, origin, end);
        Gui.InvisibleButton("##SpriteCanvas", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle | ImGuiButtonFlags.MouseButtonRight);
        var hovered = Gui.IsItemHovered(); var io = Gui.GetIO();
        if (hovered && io.MouseWheel != 0 && io.KeyCtrl) ZoomSpriteCanvas(state, preview, io.MouseWheel > 0, io.MousePos - origin);
        else if (hovered && io.MouseWheel != 0) preview.Pan(new Vector2(0, io.MouseWheel * 48 * state.ViewDpi));
        if (hovered && io.MouseWheelH != 0) preview.Pan(new Vector2(io.MouseWheelH * 48 * state.ViewDpi, 0));
        if (hovered && Gui.IsMouseClicked(ImGuiMouseButton.Middle)) state.Panning = true;
        if (!Gui.IsMouseDown(ImGuiMouseButton.Middle)) state.Panning = false;
        if (state.Panning) preview.Pan(new Vector2(io.MouseDelta.X, io.MouseDelta.Y));
        ApplySpriteCanvasScroll(state, preview, size, false);
        var imageOrigin = origin + size / 2 - new NVector2(preview.Width / 2f - preview.Offsets.X, preview.Height / 2f - preview.Offsets.Y) * preview.Scale;
        DrawSpriteCanvasOverlay(document, frame, state, origin, size, imageOrigin, preview.Scale, preview.Width, preview.Height, hovered);
        FinishSpriteCanvasScroll(preview, origin, size);
    }
    private void UpdateSpriteCanvasDrag(AssetMetadataDocument document, SpriteSettingFrame frame, SpriteCanvasState state,
        NVector2 pointer, NVector2 imageOrigin, float scale, float dpi, int imageWidth, int imageHeight)
    {
        if (!CanMutateSpriteCanvasPresentation(document, frame, state) || state.DragField == null
            || !document.CanEditSpriteSetting(state.DragField)) return;
        // Thumb raises current local point minus the original local click point,
        // after layout has moved the thumb. Round that residual at each event,
        // then apply it to the current authored value, including clamped drags.
        var delta = (pointer - SpriteCanvasThumbOrigin(frame, imageOrigin, scale, dpi, state.Drag) - state.DragLocalPoint * dpi) / scale;
        var dx = (int)Math.Round(delta.X, MidpointRounding.AwayFromZero);
        var dy = (int)Math.Round(delta.Y, MidpointRounding.AwayFromZero); object value;
        if (state.Drag == SpriteCanvasDrag.Pivot) value = (Vector2)state.DragField!.Node.Retrieve()! + new Vector2(dx, dy);
        else if ((int)state.Drag >= (int)SpriteCanvasDrag.BorderLeft)
        {
            var borders = (Vector4)state.DragField!.Node.Retrieve()!; var region = frame.Value.TextureRegion;
            var original = borders;
            if (state.Drag.HasFlag(SpriteCanvasDrag.BorderLeft)) borders.X = Math.Clamp(original.X + dx, 0, Math.Max(0, region.Width - original.Z));
            if (state.Drag.HasFlag(SpriteCanvasDrag.BorderRight)) borders.Z = Math.Clamp(original.Z - dx, 0, Math.Max(0, region.Width - original.X));
            if (state.Drag.HasFlag(SpriteCanvasDrag.BorderTop)) borders.Y = Math.Clamp(original.Y + dy, 0, Math.Max(0, region.Height - original.W));
            if (state.Drag.HasFlag(SpriteCanvasDrag.BorderBottom)) borders.W = Math.Clamp(original.W - dy, 0, Math.Max(0, region.Height - original.Y));
            value = borders;
        }
        else
        {
            var region = (Rectangle)state.DragField!.Node.Retrieve()!;
            var left = Math.Clamp(region.X, 0, imageWidth); var top = Math.Clamp(region.Y, 0, imageHeight);
            var right = Math.Clamp(region.X + region.Width, left, imageWidth); var bottom = Math.Clamp(region.Y + region.Height, top, imageHeight);
            if (state.Drag == SpriteCanvasDrag.Move)
            {
                var width = right - left; var height = bottom - top;
                left = Math.Clamp(left + dx, 0, imageWidth - width); top = Math.Clamp(top + dy, 0, imageHeight - height); right = left + width; bottom = top + height;
            }
            else
            {
                if (state.Drag.HasFlag(SpriteCanvasDrag.Left)) left = Math.Clamp(left + dx, 0, right);
                if (state.Drag.HasFlag(SpriteCanvasDrag.Right)) right = Math.Clamp(right + dx, left, imageWidth);
                if (state.Drag.HasFlag(SpriteCanvasDrag.Top)) top = Math.Clamp(top + dy, 0, bottom);
                if (state.Drag.HasFlag(SpriteCanvasDrag.Bottom)) bottom = Math.Clamp(bottom + dy, top, imageHeight);
            }
            value = new Rectangle(left, top, right - left, bottom - top);
        }
        Execute(() =>
        {
            if (!CanMutateSpriteCanvasPresentation(document, frame, state) || !document.CanEditSpriteSetting(state.DragField!))
                throw new InvalidOperationException("The sprite presentation is no longer active.");
            document.UpdateSpriteCanvasSetting(state.DragField!, value);
        });
    }
}
