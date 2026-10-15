using Stride.Assets.Entities;
using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed class SceneDocument : EntityHierarchyDocument
{
    public SceneDocument(string path, EditorSessionHistory? history = null, bool readOnly = false)
        : base(path, typeof(SceneAsset), history, readOnly) { }
    internal SceneDocument(string path, AssetLoadResult<Asset> loaded, EditorSessionHistory history, bool readOnly)
        : base(path, typeof(SceneAsset), loaded, history, readOnly) { }
    public new SceneAsset Asset => (SceneAsset)base.Asset;
}
