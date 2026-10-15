using Hexa.NET.ImGui;
using Stride.Assets.Rendering;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static readonly string[] CompositorListCollections = [nameof(GraphicsCompositorAsset.RenderStages), nameof(GraphicsCompositorAsset.RenderFeatures), nameof(GraphicsCompositorAsset.Cameras)];
    private static readonly string[] CompositorListTitles = ["Render stages:", "Render features:", "Camera slots:"];
    private static IReadOnlyList<CompositorListItem> CompositorListRows(AssetMetadataDocument document, CompositorEditorState state, string collection)
    {
        if (!state.ListRows.TryGetValue(collection, out var rows)) state.ListRows.Add(collection, rows = document.CompositorItems.Where(item => item.Collection == collection).ToArray());
        return rows;
    }
    private float CompositorAutoListHeight(AssetMetadataDocument document, CompositorEditorState state, string collection)
    {
        // Original Auto rows measure their list content, not equal thirds. The
        // two DockPanel/ListBox 8-DIP margins and native 3-DIP item padding are
        // included around the actual current font and row count.
        return UiScale(32) + Gui.GetFrameHeight() + CompositorListRows(document, state, collection).Count * (Gui.GetTextLineHeight() + UiScale(6));
    }
    private void DrawCompositorSplitLayout(AssetMetadataDocument document, CompositorEditorState state)
    {
        var available = Gui.GetContentRegionAvail(); var layoutOrigin = Gui.GetCursorScreenPos(); var scale = Gui.GetStyle().FontScaleDpi;
        var splitter = Math.Min(UiScale(5), Math.Max(1, available.X));
        var extent = Math.Max(0, available.X - splitter); var extentDips = extent / scale;
        var width = extent * Math.Clamp(state.ListColumnFraction, 0, 1);
        if (width >= 4 && available.Y >= 4)
        {
            if (Gui.BeginChild("Compositor lists", new NVector2(width, available.Y), ImGuiChildFlags.Borders,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                var origin = Gui.GetCursorScreenPos(); var innerWidth = Gui.GetContentRegionAvail().X;
                var y = origin.Y; var rowSplitter = UiScale(5);
                for (var section = 0; section < 3; section++)
                {
                    var desired = state.ListSectionHeights[section];
                    var height = float.IsNaN(desired) ? CompositorAutoListHeight(document, state, CompositorListCollections[section]) : Math.Max(0, UiScale(desired));
                    Gui.SetCursorScreenPos(new NVector2(origin.X, y)); Gui.PushID(section);
                    if (height >= 4)
                    {
                        if (Gui.BeginChild("List section", new NVector2(Math.Max(1, innerWidth), height), ImGuiChildFlags.None,
                            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
                            DrawCompositorList(document, state, CompositorListCollections[section], EditorLocalization.Text(CompositorListTitles[section]));
                        ImGuiScopes.EndChild();
                    }
                    y += height;
                    if (section < 2)
                    {
                        Gui.SetCursorScreenPos(new NVector2(origin.X, y));
                        // Auto/Auto definitions use native Resize1: only the
                        // preceding row becomes pixels, the next remains Auto.
                        EditorSplitter.Draw("##Row splitter", new NVector2(Math.Max(1, innerWidth), rowSplitter), false, false,
                            state.RowSplitters[section], ref state.ListSectionHeights[section], height / scale, available.Y / scale);
                        y += rowSplitter;
                    }
                    Gui.PopID();
                }
            }
            ImGuiScopes.EndChild();
        }
        Gui.SetCursorScreenPos(layoutOrigin + new NVector2(width, 0));
        EditorSplitter.Draw("##Column splitter", new NVector2(splitter, Math.Max(1, available.Y)), true, true,
            state.ColumnSplitter, ref state.ListColumnFraction, width / scale, extentDips, 0, extentDips);
        if (extent - width >= 4 && available.Y >= 4)
        {
            Gui.SetCursorScreenPos(layoutOrigin + new NVector2(width + splitter, 0));
            if (Gui.BeginChild("Compositor graph", new NVector2(extent - width, available.Y), ImGuiChildFlags.Borders,
                ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoScrollbar)) DrawCompositorCanvas(document, state);
            ImGuiScopes.EndChild();
        }
    }
}
