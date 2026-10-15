using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void HandleSpriteListShortcuts(AssetMetadataDocument document)
    {
        var io = Gui.GetIO();
        if (!io.KeyCtrl && !io.KeyAlt && !io.KeyShift && !io.KeySuper && Gui.IsKeyPressed(ImGuiKey.F, false)) FocusSelectedSpriteRegion(document);
        if (!io.KeyCtrl && !io.KeyAlt && !io.KeyShift && !io.KeySuper && Gui.IsKeyPressed(ImGuiKey.M, false))
        {
            var state = GetSpriteCanvasState(document);
            state.Tool = state.Tool == SpriteCanvasTool.MagicWand ? SpriteCanvasTool.None : SpriteCanvasTool.MagicWand;
        }
        if (io.KeyCtrl)
        {
            if (Gui.IsKeyPressed(ImGuiKey.C, false)) Execute(() => CopySpriteFrames(document));
            if (Gui.IsKeyPressed(ImGuiKey.V, false) && document.CanEditSpriteSettings) { UseTextureSettingsCommandContext(); Execute(() => PasteSpriteFrames(document)); }
            if (Gui.IsKeyPressed(ImGuiKey.A, false)) Execute(document.SelectAllSpriteFrames);
            if (Gui.IsKeyPressed(ImGuiKey.J, false)) Execute(() => document.SelectNextSpriteFrame(-1));
            if (Gui.IsKeyPressed(ImGuiKey.K, false)) Execute(() => document.SelectNextSpriteFrame(1));
            if (Gui.IsKeyPressed(ImGuiKey.U, false) && document.CanEditSpriteCollection) { UseTextureSettingsCommandContext(); Execute(document.AddSpriteFrame); }
            if (Gui.IsKeyPressed(ImGuiKey.D, false) && document.CanEditSpriteCollection) { UseTextureSettingsCommandContext(); Execute(document.DuplicateSelectedSpriteFrames); }
            if (Gui.IsKeyPressed(ImGuiKey.W, false) && document.CanEditSpriteSettings) { UseTextureSettingsCommandContext(); Execute(() => QueueWholeSpriteImages(document)); }
        }
        if (Gui.IsKeyPressed(ImGuiKey.Delete, false) && document.CanEditSpriteSettings)
        { UseTextureSettingsCommandContext(); Execute(document.DeleteSelectedSpriteFrames); }
    }
    private void FocusSelectedSpriteRegion(AssetMetadataDocument document)
    {
        if (document.SelectedSpriteFrame is not { } frame) return;
        var state = GetSpriteCanvas(document, frame); var preview = state.Preview;
        var region = frame.Value.TextureRegion;
        FocusSpriteCanvasRegion(state, preview, new Stride.Core.Mathematics.Vector2(region.X + region.Width * .5f, region.Y + region.Height * .5f));
    }
}
