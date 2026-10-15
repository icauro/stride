namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool IsCurrentMetadataControlOwner(AssetMetadataDocument owner)
        => workspace != null && ReferenceEquals(owner.SessionHistory, sessionHistory)
        && sessionHistory?.Registry.Find(owner.AuthoredAsset.Id) is { IsPublished: true, IsDisposed: false } state
        && ReferenceEquals(state.Document, owner)
        && (ReferenceEquals(documents.Active?.Document, owner)
            || PropertyGrid.Visible && IsCurrentAssetPropertyContext && assetBrowser.SelectedEntries.Any(entry =>
                string.Equals(entry.Path, owner.Path, StringComparison.OrdinalIgnoreCase)));
}
