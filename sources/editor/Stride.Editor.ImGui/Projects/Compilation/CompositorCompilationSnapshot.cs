using Stride.Assets.Rendering;
using Stride.Core.Assets;
using Stride.Core.Assets.Yaml;
using Stride.Core.Yaml;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal static class CompositorCompilationSnapshot
{
    internal static byte[] Capture(Asset asset, AttachedYamlAssetMetadata metadata, string? assetNamespace)
    {
        var copy = AssetCloner.Clone(asset);
        if (copy is GraphicsCompositorAsset compositor) compositor.BlockPositions.Clear();
        var normalized = new AttachedYamlAssetMetadata(); metadata.CopyInto(normalized);
        var path = new YamlAssetPath(); path.PushMember(nameof(GraphicsCompositorAsset.BlockPositions));
        if (metadata.RetrieveMetadata(AssetObjectSerializerBackend.OverrideDictionaryKey) is { } overrides)
        {
            var filtered = new YamlAssetMetadata<OverrideType>();
            foreach (var entry in overrides) if (!entry.Key.StartsWith(path)) filtered.Set(entry.Key, entry.Value);
            normalized.AttachMetadata(AssetObjectSerializerBackend.OverrideDictionaryKey, filtered);
        }
        using var stream = new MemoryStream();
        AssetFileSerializer.Save(stream, copy, normalized, assetNamespace: assetNamespace);
        return stream.ToArray();
    }
}
