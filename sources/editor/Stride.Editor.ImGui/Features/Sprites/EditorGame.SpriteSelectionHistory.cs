namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private SpriteSelectionSnapshot[] observedSpriteSelections = [];
    private SpriteSelectionSnapshot[] CaptureSpriteSelections()
        => documents.Entries.Select(entry => entry.Document).OfType<AssetMetadataDocument>()
            .Where(source => source.SpriteSettings != null)
            .Select(source => new SpriteSelectionSnapshot(source.AuthoredAsset.Id, source.SelectedSpriteIds.ToArray()))
            .Where(snapshot => snapshot.Frames.Length != 0).OrderBy(snapshot => snapshot.AssetId.ToString(), StringComparer.Ordinal).ToArray();
    private bool SpriteSelectionChanged()
    {
        var actual = CaptureSpriteSelections();
        return actual.Length != observedSpriteSelections.Length || actual.Zip(observedSpriteSelections).Any(pair => !pair.First.SameSelection(pair.Second));
    }
    private bool CanResolveSpriteSelection(SpriteSelectionSnapshot snapshot)
        => authoring?.Find(snapshot.AssetId) is { IsPublished: true, Document: AssetMetadataDocument { SpriteSettings: not null } source }
            && documents.Find(source) != null && snapshot.Frames.Any(id => source.SpriteSettingsFrames.Any(frame => frame.Id == id));
    private void RestoreSpriteSelections(SpriteSelectionSnapshot[] snapshots)
    {
        foreach (var source in documents.Entries.Select(entry => entry.Document).OfType<AssetMetadataDocument>().Where(source => source.SpriteSettings != null))
            if (authoring?.Find(source.AuthoredAsset.Id) is { IsPublished: true, Document: var actual } && ReferenceEquals(actual, source))
                source.RestoreNativeSpriteSelection(snapshots.FirstOrDefault(snapshot => snapshot.AssetId == source.AuthoredAsset.Id)?.Frames ?? []);
    }
}
