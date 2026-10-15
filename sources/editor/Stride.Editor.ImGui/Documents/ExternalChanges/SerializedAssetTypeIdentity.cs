using Stride.Core.Assets;
using Stride.Core.Yaml;
using Stride.Core.Yaml.Events;

namespace Stride.Editor.ImGui;

internal static class SerializedAssetTypeIdentity
{
    internal static bool Matches(byte[] authoredYaml, Asset restored)
    {
        using var stream = new MemoryStream(authoredYaml, false);
        using var reader = new StreamReader(stream);
        var parser = new Parser(reader);
        string? rootTag = null;
        while (parser.MoveNext())
            if (parser.Current is NodeEvent root) { rootTag = root.Tag; break; }
        if (string.IsNullOrEmpty(rootTag)) return false;
        if (restored is IUnloadable proxy)
        {
            var proxyTag = proxy.ParsingEvents.OfType<NodeEvent>().FirstOrDefault()?.Tag;
            return !string.IsNullOrEmpty(proxyTag)
                && string.Equals(Uri.UnescapeDataString(rootTag), Uri.UnescapeDataString(proxyTag), StringComparison.Ordinal);
        }
        var resolved = AssetYamlSerializer.Default.GetSerializerSettings().TagTypeRegistry.TypeFromTag(rootTag, out _);
        return resolved == restored.GetType();
    }
}
