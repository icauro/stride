using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;

namespace Stride.Editor.ImGui;

/// <summary>An authored owner of the actual session Quantum asset graph.</summary>
internal interface IEditorAssetGraphDocument : IAssetTagDocument
{
    AssetPropertyGraph AuthoringGraph { get; }
    void ApplyInheritedAssetMutation(Action mutate, string title);
    void UpdateAuthoringAssetIdentity(AssetItem item);
}

internal partial class AssetMetadataDocument : IEditorAssetGraphDocument
{
    AssetPropertyGraph IEditorAssetGraphDocument.AuthoringGraph => AuthoringGraph;
    void IEditorAssetGraphDocument.ApplyInheritedAssetMutation(Action mutate, string title) => ApplyInheritedAssetMutation(mutate, title);
    void IEditorAssetGraphDocument.UpdateAuthoringAssetIdentity(AssetItem item) => UpdateAuthoringAssetIdentity(item);
}
