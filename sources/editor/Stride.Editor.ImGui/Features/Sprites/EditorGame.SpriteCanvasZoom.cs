using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;
using SVector2 = Stride.Core.Mathematics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static void SynchronizeSpriteCanvasDpi(SpriteCanvasState state, TextureAssetPreview? preview)
    {
        var dpi = Gui.GetStyle().FontScaleDpi;
        if (preview != null) preview.CheckerboardPixelScale = dpi;
        if (state.ViewDpi == dpi) return;
        state.ViewScroll *= dpi / state.ViewDpi;
        state.ViewDpi = dpi; state.RestoreViewScroll = true;
        if (preview != null) preview.SetView(state.ViewScale * dpi, preview.Offsets);
    }
    private static void ChangeSpriteCanvasScale(SpriteCanvasState state, TextureAssetPreview? preview, float scale, NVector2? pointer = null)
    {
        var center = pointer ?? state.ScrollSize * .5f;
        state.ViewScroll = scale / state.ViewScale * (state.ViewScroll + center) - center;
        state.ViewScale = scale; state.RestoreViewScroll = true;
        if (preview != null) preview.SetView(scale * state.ViewDpi, preview.Offsets);
    }
    private static void ZoomSpriteCanvas(SpriteCanvasState state, TextureAssetPreview? preview, bool zoomIn, NVector2? pointer = null)
    {
        var factors = Stride.Core.Presentation.Core.Utils.ZoomFactors;
        var scale = zoomIn ? factors.FirstOrDefault(value => (float)value > state.ViewScale, factors[^1])
            : factors.LastOrDefault(value => (float)value < state.ViewScale, factors[0]);
        ChangeSpriteCanvasScale(state, preview, (float)scale, pointer);
    }
    private static void FocusSpriteCanvasRegion(SpriteCanvasState state, TextureAssetPreview? preview, SVector2 center)
    {
        state.ViewScroll = new NVector2(center.X, center.Y) * (preview?.Scale ?? state.ViewScale * state.ViewDpi) - state.ScrollSize * .5f;
        state.RestoreViewScroll = true;
    }
}
