using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;
using System.Globalization;

namespace Stride.Editor.ImGui;

internal sealed partial class AssetBrowserPanel
{
    private (long Content, float Font, float Dpi, int Identity, string Locale, int Thumbnail)? tableMetricsKey;
    private float tableLabelHeight;
    private (long Content, float Dpi, int Identity, string Locale)? tileMetricsKey;
    private float tileNameHeight, tileTypeHeight;
    private void MeasureTileAreas()
    {
        var dpi = Gui.GetStyle().FontScaleDpi;
        var key = (assetBrowser.ContentVersion, dpi, Gui.GetFont().GetHashCode(), CultureInfo.CurrentUICulture.Name);
        if (tileMetricsKey == key) return;
        var rows = assetBrowser.VisibleContent;
        float nameHeight, typeHeight;
        using (ImGuiScopes.Font(12 * dpi))
        {
            nameHeight = host.MeasureLabel("Mg").Y;
            foreach (var row in rows)
            {
                var name = row.Asset is { } asset ? AssetBrowserNames.Display(asset) + (host.IsDirty(asset) ? "*" : "") : row.Folder!.Name;
                nameHeight = Math.Max(nameHeight, host.MeasureLabel(name).Y);
            }
        }
        using (ImGuiScopes.Font(10 * dpi))
        {
            typeHeight = host.MeasureLabel("Folder").Y;
            foreach (var row in rows) if (row.Asset is { } asset) typeHeight = Math.Max(typeHeight, host.MeasureLabel(asset.TypeLabel).Y);
        }
        tileNameHeight = Math.Max(36 * dpi, nameHeight * 2 + 2 * dpi);
        tileTypeHeight = Math.Max(14 * dpi, typeHeight);
        tileMetricsKey = key;
    }
    private float TileNameHeight()
    {
        MeasureTileAreas(); return tileNameHeight;
    }
    private float TileTypeHeight()
    {
        MeasureTileAreas(); return tileTypeHeight;
    }
    private void PaintCenteredTileLabel(string text, Vector2 origin, float width, float height, uint color)
    {
        var size = host.MeasureLabel(text);
        host.PaintLabel(text, origin + new Vector2(Math.Max(0, (width - size.X) / 2), 0), origin, origin + new Vector2(width, height), color);
    }
    private float BrowserTableRowHeight()
    {
        var key = (assetBrowser.ContentVersion, Gui.GetFontSize(), Gui.GetStyle().FontScaleDpi, Gui.GetFont().GetHashCode(), CultureInfo.CurrentUICulture.Name, assetGridThumbnailSize);
        if (tableMetricsKey != key)
        {
            var height = Math.Max(Math.Max(Gui.GetFrameHeight(), assetGridThumbnailSize * Gui.GetStyle().FontScaleDpi), host.MeasureLabel("Mg").Y);
            foreach (var row in assetBrowser.VisibleContent)
            {
                var name = row.Asset is { } asset ? AssetBrowserNames.Display(asset) + (host.IsDirty(asset) ? "*" : "") : row.Folder!.Name;
                height = Math.Max(height, host.MeasureLabel(name).Y);
                height = Math.Max(height, host.MeasureLabel(row.Asset?.TypeLabel ?? "Folder").Y);
            }
            tableLabelHeight = height; tableMetricsKey = key;
        }
        return tableLabelHeight;
    }
}
