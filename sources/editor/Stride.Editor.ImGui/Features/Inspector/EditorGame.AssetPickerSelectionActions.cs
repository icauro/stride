namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool TryRunPickerSelectionAction(PickerContextAction action, AssetBrowserState selection,
        Func<bool> isCurrent, Action<bool> completed)
    {
        if (action is not (PickerContextAction.Delete or PickerContextAction.Explore)) return false;
        var owner = workspace;
        var rows = selection.SelectedContent.ToArray();
        var order = rows.Select(row => row.Id).ToArray();
        var items = rows.Where(row => row.Asset != null).Select(row =>
            (Entry: row.Asset!, Item: owner?.AssetItems.FirstOrDefault(item => item.Id == row.Asset!.Id && !item.IsDeleted))).ToArray();
        var folders = rows.Where(row => row.Folder != null).Select(row =>
            (Captured: row.Folder!, Current: assetBrowser.FindFolder(row.Folder!.Id))).ToArray();
        bool Current()
        {
            if (owner == null || !ReferenceEquals(workspace, owner) || !isCurrent()
                || !order.SequenceEqual(selection.SelectedContent.Select(row => row.Id))) return false;
            foreach (var (entry, item) in items)
                if (item == null || item.IsDeleted || !ReferenceEquals(owner.Session.FindAsset(entry.Id), item)
                    || !SamePath(item.FullPath.ToOSPath(), entry.Path)) return false;
            foreach (var (captured, current) in folders)
                if (current == null || !ReferenceEquals(assetBrowser.FindFolder(captured.Id), current)
                    || !ReferenceEquals(current.Package, captured.Package) || current.RelativePath != captured.RelativePath
                    || current.IsCode != captured.IsCode) return false;
            return rows.Length != 0;
        }
        Execute(() =>
        {
            try
            {
                if (!Current()) throw new InvalidOperationException("The asset picker selection changed.");
                if (action == PickerContextAction.Delete)
                {
                    if (!rows.All(row => PickerCapabilities(row).Delete))
                        throw new InvalidOperationException("The selected content can't be deleted.");
                    RequestAssetDeletion(selection, Current, completed);
                }
                else
                {
                    var target = rows[^1];
                    if (target.Asset is { } asset) ExploreAssetEntry(asset);
                    else if (target.Folder is { } folder) ExploreAssetFolder(folder);
                    completed(true);
                }
            }
            catch { completed(false); throw; }
        });
        return true;
    }
}
