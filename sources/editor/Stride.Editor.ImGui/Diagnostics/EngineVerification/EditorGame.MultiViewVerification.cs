using Stride.Assets.Entities;
using Stride.Core.Assets;
using Stride.Core.IO;
using Stride.Core.Mathematics;

namespace Stride.Editor.ImGui;

internal sealed record MultiViewFixtureVerification(AssetId SceneId, string ScenePath, string PackagePath);

internal sealed partial class EditorGame
{
    internal Guid? VerificationDocumentId(EntityHierarchyDocument owner) => documents.Find(owner)?.Id;
    internal EngineViewport? VerificationDocumentViewport(EntityHierarchyDocument owner)
    {
        var entry = documents.Find(owner);
        return entry != null && sceneDocumentViews.TryGetValue(entry.Id, out var view) && IsCurrentSceneDocumentView(view)
            ? view.Viewport : null;
    }
    internal EntityHierarchyDocument? VerificationActiveHierarchy => documents.Active?.Document as EntityHierarchyDocument;
    internal Stride.Core.Assets.AssetId? VerificationAssetBrowserPrimary => assetBrowser.Primary;
}

internal sealed partial class EngineViewport
{
    internal object? VerificationCompositorOwner => loadedCompositor;
}
