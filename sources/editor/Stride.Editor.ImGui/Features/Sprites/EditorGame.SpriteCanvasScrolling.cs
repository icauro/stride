using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void ApplySpriteCanvasScroll(SpriteCanvasState state, TextureAssetPreview preview, NVector2 size, bool readNative = true)
    {
        var scale = preview.Scale;
        var margin = 10 * Gui.GetStyle().FontScaleDpi;
        var maximum = new NVector2(Math.Max(0, preview.Width * scale + margin * 2 - size.X), Math.Max(0, preview.Height * scale + margin * 2 - size.Y));
        NVector2 scroll;
        if (state.RestoreViewScroll)
        { scroll = state.ViewScroll; state.RestoreViewScroll = false; }
        else if (readNative && state.ScrollInitialized && state.ScrollScale == scale && state.ScrollOffsets == preview.Offsets && state.ScrollSize == size)
            scroll = new(Gui.GetScrollX(), Gui.GetScrollY());
        else
            scroll = new((preview.Width * scale - size.X) * .5f + margin - preview.Offsets.X * scale,
                (preview.Height * scale - size.Y) * .5f + margin - preview.Offsets.Y * scale);
        scroll = NVector2.Clamp(scroll, NVector2.Zero, maximum);
        Gui.SetScrollX(scroll.X); Gui.SetScrollY(scroll.Y);
        preview.SetOffsets(new Vector2((preview.Width * scale - size.X) * .5f + margin - scroll.X,
            (preview.Height * scale - size.Y) * .5f + margin - scroll.Y) / scale);
        state.ScrollInitialized = true; state.ScrollScale = scale; state.ScrollOffsets = preview.Offsets; state.ScrollSize = size;
        state.ViewScroll = scroll; state.ViewScale = scale / Gui.GetStyle().FontScaleDpi;
    }
    private static void FinishSpriteCanvasScroll(TextureAssetPreview preview, NVector2 origin, NVector2 size)
    {
        Gui.SetCursorScreenPos(origin - new NVector2(Gui.GetScrollX(), Gui.GetScrollY()));
        var margins = 20 * Gui.GetStyle().FontScaleDpi;
        Gui.Dummy(NVector2.Max(size, new(preview.Width * preview.Scale + margins, preview.Height * preview.Scale + margins)));
    }
}
