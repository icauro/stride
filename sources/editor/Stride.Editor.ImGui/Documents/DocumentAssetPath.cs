using Stride.Core.Assets;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal static class DocumentAssetPath
{
    /// <summary>Changes the detached graph item's physical origin without replacing its live nodes.</summary>
    public static void Update(AssetItem? item, string path)
    {
        if (item == null) return;
        var directory = new UDirectory(System.IO.Path.GetDirectoryName(path)!);
        var filename = new UFile(System.IO.Path.GetFileName(path));
        item.SourceFolder = directory; item.AlternativePath = filename;
    }
}
