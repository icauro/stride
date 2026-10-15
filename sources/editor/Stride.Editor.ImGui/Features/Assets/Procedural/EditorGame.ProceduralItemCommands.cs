namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawProceduralItemPresenterCommands(ProceduralAssetDocument asset, ProceduralCollectionItem item)
    {
        bool Current() => ReferenceEquals(assetDocument, asset) && asset.IsCollectionItemCurrent(item)
            && sessionHistory?.Registry.Find(asset.Asset.Id) is { IsPublished: true, IsDisposed: false } state
            && ReferenceEquals(state.Document, asset);
        DrawNativePresenterCommands(asset, Current,
            () => AcquireHierarchyFactoryTargets(1, _ => asset.AcquireCollectionItemTarget(item)),
            () => new EditorAssetCommandMutation(asset.BeginExtensionCommandMutation()));
    }
}
