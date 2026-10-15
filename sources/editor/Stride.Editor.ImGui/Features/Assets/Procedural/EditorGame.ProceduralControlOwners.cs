namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool IsCurrentProceduralControlOwner(ProceduralAssetDocument owner)
        => ReferenceEquals(assetDocument, owner) && ReferenceEquals(owner.SessionHistory, sessionHistory)
        && sessionHistory?.Registry.Find(((IAssetTagDocument)owner).AuthoredAsset.Id) is { IsPublished: true, IsDisposed: false } state
        && ReferenceEquals(state.Document, owner);
}
