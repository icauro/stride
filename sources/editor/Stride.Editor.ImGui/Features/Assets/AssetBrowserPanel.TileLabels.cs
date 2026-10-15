using System.Globalization;
using Stride.Core.Assets;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class AssetBrowserPanel
{
    private sealed record TileLabel(string Name, float Width, float FontSize, float Dpi, string Locale, int Font, string[] Lines);
    private readonly Dictionary<AssetId, TileLabel> tileLabels = [];

    private void DrawAssetTileName(AssetEntry asset, NVector2 origin, float width)
    {
        using var font = ImGuiScopes.Font(12 * Gui.GetStyle().FontScaleDpi);
        var fontSize = Gui.GetFontSize();
        var name = AssetBrowserNames.Display(asset);
        if (host.IsDirty(asset)) name += "*";
        var dpi = Gui.GetStyle().FontScaleDpi; var locale = CultureInfo.CurrentUICulture.Name; var fontIdentity = Gui.GetFont().GetHashCode();
        if (!tileLabels.TryGetValue(asset.Id, out var label) || label.Name != name || label.Width != width || label.FontSize != fontSize
            || label.Dpi != dpi || label.Locale != locale || label.Font != fontIdentity)
            tileLabels[asset.Id] = label = new TileLabel(name, width, fontSize, dpi, locale, fontIdentity, WrapTileLabel(name, width, MeasureTileLabel));
        foreach (var line in label.Lines)
        {
            var x = Math.Max(0, (width - MeasureTileLabel(line)) * 0.5f);
            var size = host.MeasureLabel(line);
            host.PaintLabel(line, origin + new NVector2(x, 0), origin, origin + new NVector2(width, size.Y), Gui.GetColorU32(Hexa.NET.ImGui.ImGuiCol.Text));
            origin.Y += size.Y + 2 * Gui.GetStyle().FontScaleDpi;
        }
    }

    private float MeasureTileLabel(string text) => host.MeasureLabel(text).X;
    private string EllipsizeNativeTileLabel(string text, float width) => EllipsizeTileLabel(text, width, MeasureTileLabel);

    internal static string[] WrapTileLabel(string text, float width, Func<string, float>? measure = null)
    {
        measure ??= static value => Gui.CalcTextSize(value).X;
        if (measure(text) <= width) return [text];
        var elements = StringInfo.ParseCombiningCharacters(text);
        var end = 0; var preferred = 0;
        foreach (var index in elements)
        {
            if (index == 0) continue;
            if (measure(text[..index]) > width) continue;
            end = index;
            var before = text[index - 1]; var after = text[index];
            if (!char.IsLetterOrDigit(before) || char.IsLower(before) && (char.IsUpper(after) || char.IsDigit(after))
                || char.IsUpper(before) && char.IsDigit(after)) preferred = index;
        }
        end = preferred > 0 ? preferred : end;
        if (end == 0) return [EllipsizeTileLabel(text, width, measure)];
        return [text[..end].TrimEnd(), EllipsizeTileLabel(text[end..].TrimStart(), width, measure)];
    }

    internal static string EllipsizeTileLabel(string text, float width, Func<string, float>? measure = null)
    {
        measure ??= static value => Gui.CalcTextSize(value).X;
        if (measure(text) <= width) return text;
        const string ellipsis = "…";
        var end = 0;
        foreach (var index in StringInfo.ParseCombiningCharacters(text))
        {
            if (measure(text[..index] + ellipsis) <= width) end = index;
        }
        return text[..end] + ellipsis;
    }
}
