using System.Xml.Linq;
using Stride.SpriteStudio.Assets;

namespace Stride.Editor.ImGui;

internal static class SpriteStudioThumbnailSources
{
    public static IEnumerable<string> Files(SpriteStudioModelAsset asset, string assetPath)
    {
        if (string.IsNullOrEmpty(asset.Source)) yield break;
        var source = Path.GetFullPath(asset.Source.ToOSPath(), Path.GetDirectoryName(assetPath)!);
        yield return source;
        if (!File.Exists(source)) yield break;
        var document = XDocument.Load(source);
        if (document.Root == null) yield break;
        var ns = document.Root.Name.Namespace;
        var directory = Path.GetDirectoryName(source)!;
        foreach (var entry in document.Descendants(ns + "cellmapNames").Descendants(ns + "value"))
        {
            var cellmap = Path.GetFullPath(entry.Value, directory);
            yield return cellmap;
            if (!File.Exists(cellmap)) continue;
            var cells = XDocument.Load(cellmap);
            if (cells.Root == null) continue;
            // Match SpriteStudioXmlImport.ParseCellMaps: both cell maps and
            // their image paths resolve relative to the SSAE directory.
            foreach (var image in cells.Descendants(cells.Root.Name.Namespace + "imagePath"))
                yield return Path.GetFullPath(image.Value, directory);
        }
    }
}
