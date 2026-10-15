using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class AssetBrowserState
{
    internal Action? BeforeSelectionChange { get; set; }
    internal Action? AfterSelectionChange { get; set; }
    private int selectionMutationDepth;
    private sealed class BrowserSelectionScope(AssetBrowserState owner) : IDisposable
    {
        public void Dispose() { if (--owner.selectionMutationDepth == 0) owner.AfterSelectionChange?.Invoke(); }
    }
    private IDisposable? BeforeBrowserSelectionChange()
    {
        if (BeforeSelectionChange == null && AfterSelectionChange == null) return null;
        var outermost = selectionMutationDepth++ == 0;
        try { if (outermost) BeforeSelectionChange?.Invoke(); }
        catch { selectionMutationDepth--; throw; }
        return new BrowserSelectionScope(this);
    }
    private bool SameBrowserSelection(IEnumerable<AssetId> assets, IEnumerable<string> folders, AssetId? primary, string? folderPrimary)
        => selected.SetEquals(assets) && selectedContentFolders.SetEquals(folders) && Primary == primary
        && string.Equals(PrimaryContentFolder, folderPrimary, StringComparison.OrdinalIgnoreCase);

    internal AssetId[] NormalizeSelectionIds(IEnumerable<AssetId> ids)
    {
        if (source == null) return [];
        var available = source.Select(asset => asset.Id).ToHashSet();
        return ids.Where(available.Contains).Distinct().ToArray();
    }
}
