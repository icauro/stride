namespace Stride.Editor.ImGui;

internal sealed partial class UIEditorPanel
{
    private void SaveEditorSelection()
    {
        if (document == null) return;
        var state = ViewSettings;
        state.SelectionIds = selected.ToArray();
        state.DisplayedRoot = displayedRoot;
        state.WholeAssetProperties = showUIAssetProperties;
    }
    private void RestoreEditorSelection(UIDocument source)
    {
        var state = ViewSettings;
        selected.UnionWith(state.SelectionIds.Where(id => source.Find(id) != null));
        displayedRoot = state.DisplayedRoot is { } root && source.Roots.Any(element => element.Id == root)
            ? root : source.Roots.FirstOrDefault()?.Id;
        RefreshSelection();
        showUIAssetProperties = state.WholeAssetProperties;
        if (showUIAssetProperties) source.SelectExtensionElements([]);
    }
}
