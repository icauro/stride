using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal byte[] CaptureCompilationAsset(AssetItem canonicalItem)
    {
        if (canonicalItem.Id != Asset.Id) throw new InvalidOperationException("The compiler snapshot has another hierarchy owner.");
        // Use the same real Quantum override/item metadata as Save and assembly
        // migration, without ending the user's edit or writing their asset file.
        EnsureHierarchyGraph(); PrepareHierarchyMetadata();
        using var stream = new MemoryStream();
        AssetFileSerializer.Save(stream, Asset, hierarchyItem?.YamlMetadata ?? loaded.YamlMetadata,
            assetNamespace: canonicalItem.Package.Container?.AssetNamespace);
        return stream.ToArray();
    }
}
