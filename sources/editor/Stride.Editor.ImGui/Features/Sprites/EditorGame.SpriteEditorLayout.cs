using System.Runtime.CompilerServices;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class SpriteEditorLayoutState
    {
        internal float ListFraction = .25f;
        internal readonly EditorSplitterState Splitter = new();
    }
    private readonly ConditionalWeakTable<AssetMetadataDocument, SpriteEditorLayoutState> spriteEditorLayouts = new();
    private void DrawSpriteEditorLayout(AssetMetadataDocument document)
    {
        var state = spriteEditorLayouts.GetOrCreateValue(document);
        if (Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && !Gui.GetIO().WantTextInput && !Gui.IsAnyItemActive())
            HandleSpriteListShortcuts(document);
        var origin = Gui.GetCursorScreenPos(); var available = Gui.GetContentRegionAvail(); var dpi = Gui.GetStyle().FontScaleDpi;
        var splitter = Math.Min(5 * dpi, Math.Max(1, available.X));
        var extent = Math.Max(0, available.X - splitter);
        var listWidth = extent * Math.Clamp(state.ListFraction, 0, 1);
        if (listWidth >= 4 && available.Y >= 4)
        {
            var draw = Gui.BeginChild("##SpriteListPanel", new NVector2(listWidth, available.Y), ImGuiChildFlags.None,
                ImGuiWindowFlags.None);
            try
            {
                if (draw)
                {
                    if (Controls.Enabled)
                    {
                        var originBody = Gui.GetWindowPos();
                        var sizeBody = Gui.GetWindowSize();
                        if (sizeBody.X > 0 && sizeBody.Y > 0) Controls.Centers["sprites/list/body"] = originBody + sizeBody * .5f;
                    }
                    DrawSpriteFrameListPanel(document);
                }
            }
            finally { ImGuiScopes.EndChild(); }
        }
        Gui.SetCursorScreenPos(origin + new NVector2(listWidth, 0));
        EditorSplitter.Draw("##SpriteColumnSplitter", new NVector2(splitter, Math.Max(1, available.Y)), true, true,
            state.Splitter, ref state.ListFraction, listWidth / dpi, extent / dpi, 0, extent / dpi);
        if (extent - listWidth >= 4 && available.Y >= 4)
        {
            Gui.SetCursorScreenPos(origin + new NVector2(listWidth + splitter, 0));
            var draw = Gui.BeginChild("##SpriteCanvasPanel", new NVector2(extent - listWidth, available.Y), ImGuiChildFlags.None,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            try { if (draw) DrawSpriteCanvasContent(document); }
            finally { ImGuiScopes.EndChild(); }
        }
    }
}
