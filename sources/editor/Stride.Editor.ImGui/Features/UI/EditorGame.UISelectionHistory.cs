namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private UISelectionSnapshot[] observedUISelections = [];
    private UISelectionSnapshot[] CaptureUISelections()
    {
        return documents.Entries.Select(entry => entry.Document).OfType<UIDocument>()
            .Where(source => ownedUIEditors.TryGetValue(source, out var panel) && panel.HasNativeSelectionScope(source))
            .Select(source => new UISelectionSnapshot(source.Asset.Id, ownedUIEditors[source].CaptureNativeSelection(source)))
            .Where(snapshot => snapshot.Elements.Length != 0)
            .OrderBy(snapshot => snapshot.AssetId.ToString(), StringComparer.Ordinal).ToArray();
    }
    private bool UISelectionChanged()
    {
        var current = CaptureUISelections();
        return current.Length != observedUISelections.Length || current.Zip(observedUISelections).Any(pair => !pair.First.SameSelection(pair.Second));
    }
    private bool CanResolveUISelection(UISelectionSnapshot snapshot)
        => authoring?.Find(snapshot.AssetId) is { IsPublished: true, Document: UIDocument source }
            && ownedUIEditors.TryGetValue(source, out var panel)
            && documents.Find(source) != null && panel.HasNativeSelectionScope(source) && snapshot.Elements.Any(id => source.Find(id) != null);
    private void RestoreUISelections(UISelectionSnapshot[] snapshots)
    {
        foreach (var source in documents.Entries.Select(entry => entry.Document).OfType<UIDocument>())
            if (ownedUIEditors.TryGetValue(source, out var panel)
                && authoring?.Find(source.Asset.Id) is { IsPublished: true, Document: var current } && ReferenceEquals(current, source))
                panel.RestoreNativeSelection(source, snapshots.FirstOrDefault(snapshot => snapshot.AssetId == source.Asset.Id)?.Elements ?? []);
    }
}
