using Stride.Assets.Entities;
using Stride.Core.Assets;
using Stride.Core.Assets.Yaml;

namespace Stride.Editor.ImGui;

internal sealed class PrefabDocument : EntityHierarchyDocument
{
    internal PrefabDocument(string path, EditorSessionHistory? history = null, bool readOnly = false)
        : base(path, typeof(PrefabAsset), history, readOnly) { }
    internal PrefabDocument(string path, PrefabAsset asset, AttachedYamlAssetMetadata metadata, EditorSessionHistory history)
        : base(path, typeof(PrefabAsset), new AssetLoadResult<Asset>(asset, null, false, metadata), history) { }
    internal PrefabDocument(string path, AssetLoadResult<Asset> loaded, EditorSessionHistory history, bool readOnly)
        : base(path, typeof(PrefabAsset), loaded, history, readOnly) { }
    public new PrefabAsset Asset => (PrefabAsset)base.Asset;
}
