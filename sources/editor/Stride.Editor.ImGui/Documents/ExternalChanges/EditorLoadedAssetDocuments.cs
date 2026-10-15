using Stride.Assets.Entities;
using Stride.Assets.Models;
using Stride.Assets.Scripts;
using Stride.UI.Assets;
using Stride.Core.Assets;
using Stride.Core.Yaml;

namespace Stride.Editor.ImGui;

// Uses the exact native load result, metadata and owned disk fingerprint; never reloads a different file generation.
internal static class EditorLoadedAssetDocuments
{
    internal static IEditorDocument Create(string path, AssetLoadResult<Asset> loaded, byte[] baseline, EditorSessionHistory? history, bool readOnly)
    {
        if (loaded.Asset is IUnloadable) return new AssetMetadataDocument(path, loaded, baseline, history, readOnly);
        IEditorDocument document = loaded.Asset switch
        {
            SceneAsset => new SceneDocument(path, loaded, history!, readOnly),
            PrefabAsset => new PrefabDocument(path, loaded, history!, readOnly),
            ProceduralModelAsset procedural => new ProceduralAssetDocument(path,
                new AssetLoadResult<ProceduralModelAsset>(procedural, loaded.Logger, loaded.AliasOccurred, loaded.YamlMetadata), baseline, history, readOnly),
            UIAssetBase => new UIDocument(path, loaded, baseline, history, readOnly),
            VisualScriptAsset => new VisualScriptDocument(path, loaded, baseline, history, readOnly),
            SourceCodeAsset => throw new InvalidOperationException("A source asset cannot be rebound through asset YAML."),
            _ => new AssetMetadataDocument(path, loaded, baseline, history, readOnly),
        };
        if (document is EntityHierarchyDocument hierarchy)
            ((IEditorOwnedFileGeneration)hierarchy).AcceptOwnedSaveBaseline(baseline, true);
        return document;
    }
}
