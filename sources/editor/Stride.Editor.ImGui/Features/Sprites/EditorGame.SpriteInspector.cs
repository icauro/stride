namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool TryDrawSelectedSpriteProperties()
    {
        if (documents.Active?.Document is not AssetMetadataDocument { SpriteSettings: not null } source
            || source.SelectedSpriteIds.Count == 0
            || sessionHistory?.Registry.Find(source.AuthoredAsset.Id) is not { IsPublished: true, IsDisposed: false, Document: var owner }
            || !ReferenceEquals(owner, source)) return false;
        // Native frame selection owns these rows after it clears the competing
        // asset-browser selection, just like the native compositor/UI scopes.
        DrawAssetMetadataDocument(source, inPropertyGrid: true);
        return true;
    }
}
