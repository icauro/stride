using Stride.Core.Assets;
using Stride.Core.Yaml;
using Stride.Core.Yaml.Events;

namespace Stride.Editor.ImGui;

internal static class ExternalAssetTypeIdentity
{
    internal static bool Matches(Asset current, Asset replacement)
    {
        if (current.Id != replacement.Id) return false;
        var oldProxy = current as IUnloadable; var newProxy = replacement as IUnloadable;
        if (oldProxy == null && newProxy == null) return current.GetType() == replacement.GetType();
        static string? Tag(IUnloadable proxy) => proxy.ParsingEvents.OfType<NodeEvent>().FirstOrDefault()?.Tag;
        if (oldProxy != null && newProxy != null)
        {
            var before = Tag(oldProxy); var after = Tag(newProxy);
            return !string.IsNullOrEmpty(before) && !string.IsNullOrEmpty(after)
                && string.Equals(Uri.UnescapeDataString(before), Uri.UnescapeDataString(after), StringComparison.Ordinal);
        }
        var tag = Tag(oldProxy ?? newProxy!);
        if (string.IsNullOrEmpty(tag)) return false;
        var resolved = AssetYamlSerializer.Default.GetSerializerSettings().TagTypeRegistry.TypeFromTag(tag, out _);
        return resolved == (oldProxy == null ? current : replacement).GetType();
    }
}
