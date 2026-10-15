using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record CreationSelection(AssetBrowserState Browser, AssetId[] Assets, AssetId? Primary, string[] Folders, string? FolderPrimary,
        AssetBrowserScopeSnapshot Scope, string[] Order);

    private CreationSelection CaptureCreationSelection(AssetBrowserState? browser = null)
    {
        browser ??= AssetCreationBrowser;
        return new(browser, browser.CaptureSelection(), browser.Primary, browser.CaptureContentFolderSelection(), browser.PrimaryContentFolder,
            browser.CaptureNavigationScope(), browser.CaptureContentSelectionOrder());
    }

    private void RestoreCreationSelection(CreationSelection before)
    {
        if (workspace == null) return;
        before.Browser.RefreshEntries(workspace);
        before.Browser.RestoreSelection(before.Assets, before.Primary);
        before.Browser.RestoreNavigationScope(before.Scope);
        before.Browser.RestoreContentFolderSelection(before.Folders, before.FolderPrimary);
        before.Browser.RestoreContentSelectionOrder(before.Order);
        if (!ReferenceEquals(before.Browser, assetBrowser)) return;
        selectedAssetPath = before.Browser.PrimaryEntry?.Path;
        ActivateAssetPropertyContext();
        if (before.Browser.SelectedCount != 1 || before.Browser.SelectedContentFolders.Count != 0) ClearSelectedAssetPreview();
    }

    private void RestoreFailedCreationSelection(CreationSelection before, Exception failure)
    {
        try { RestoreCreationSelection(before); }
        catch (Exception recovery) { throw new AggregateException("Asset creation failed and its selection could not be restored.", failure, recovery); }
    }

    private void SelectCreatedAssetBatch(IEnumerable<AssetId> ids)
    {
        var created = ids.Distinct().ToArray();
        var browser = AssetCreationBrowser;
        browser.RefreshEntries(workspace!);
        browser.RestoreSelection(created, created.Length > 0 ? created[^1] : null);
        if (!ReferenceEquals(browser, assetBrowser)) return;
        selectedAssetPath = assetBrowser.PrimaryEntry?.Path;
        ActivateAssetPropertyContext();
        if (created.Length != 1) ClearSelectedAssetPreview();
    }
}
