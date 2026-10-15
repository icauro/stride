using Stride.Core.Serialization.Contents;
using Stride.Graphics;

namespace Stride.Editor.ImGui;

internal static class CompiledThumbnailReferences
{
    public static IReadOnlyList<string> Read(CompiledProjectContent content, string url, bool sheet)
        => Read(content, url, sheet ? typeof(SpriteSheet) : typeof(SpriteFont));

    public static IReadOnlyList<string> Read(CompiledProjectContent content, string url, Type runtimeType)
    {
        var cache = content.CreateObjectCache(); object? root = null;
        var urls = new HashSet<string>(StringComparer.Ordinal);
        var loader = new ContentManagerLoaderSettings
        {
            AllowContentStreaming = false,
            ContentFilter = (ILoadableReference reference, ref bool load) => { urls.Add(reference.Location); load = false; }
        };
        try
        {
            root = cache.Load(runtimeType, url, loader);
            return urls.Order(StringComparer.Ordinal).ToArray();
        }
        finally { if (root != null) cache.Unload(root); }
    }
}
