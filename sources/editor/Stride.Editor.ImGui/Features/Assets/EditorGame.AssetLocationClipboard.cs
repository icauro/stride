using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private AssetBrowserFolder[] AssetClipboardLocations(AssetBrowserFolder primary)
    {
        var selected = assetBrowser.IsFolderSelected(primary)
            ? assetBrowser.CaptureNavigationScope().Folders.Select(assetBrowser.FindFolder).OfType<AssetBrowserFolder>().ToArray() : [primary];
        // Native package locations refer to the actual Assets mount, not Code.
        var folders = selected.Select(folder => folder.Parent == null
            ? folder.Children.FirstOrDefault(mount => !mount.IsCode) ?? folder : folder).DistinctBy(folder => folder.Id).ToArray();
        return folders;
    }
    private bool CanCopyAssetLocations(AssetBrowserFolder primary)
    {
        if (workspace == null || authoring == null) return false;
        var roots = AssetClipboardLocations(primary);
        if (roots.Any(folder => roots.Any(parent => parent.Id != folder.Id && ReferenceEquals(parent.Package, folder.Package)
            && parent.IsCode == folder.IsCode && (parent.RelativePath.Length == 0
                || folder.RelativePath.StartsWith(parent.RelativePath.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))))) return false;
        return roots.Length > 0 && roots.All(folder => workspace.Session.Packages.Contains(folder.Package)
            && folder.Package.State >= Stride.Core.Assets.PackageState.AssetsReady
            && assetBrowser.FindFolder(folder.Id) is { } current && ReferenceEquals(current.Package, folder.Package)
            && CanCopyAssetFolder(folder));
    }
    private bool CanCutAssetLocations(AssetBrowserFolder primary) => CanStartAssetEdit && CanCopyAssetLocations(primary)
        && (!assetBrowser.IsFolderSelected(primary) || assetBrowser.CaptureNavigationScope().Categories.Length == 0)
        && AssetClipboardLocations(primary).All(CanCutAssetFolder);
    private void CopyAssetLocations(AssetBrowserFolder primary)
    {
        if (!CanCopyAssetLocations(primary)) return;
        var text = SerializeAssetContentClipboard(AssetClipboardLocations(primary), out _, []);
        if (text.Length != 0) Gui.SetClipboardText(text);
    }
    private void CutAssetLocations(AssetBrowserFolder primary)
    {
        if (!CanCutAssetLocations(primary)) return;
        CutAssetFolderRoots(AssetClipboardLocations(primary), []);
    }
    private bool CanPasteAssetLocation(AssetBrowserFolder primary) => (!assetBrowser.IsFolderSelected(primary)
        || assetBrowser.CaptureNavigationScope().Folders.Length == 1) && CanPasteAssets(primary);
    private void PasteAssetLocation(AssetBrowserFolder primary)
    {
        if (!CanPasteAssetLocation(primary)) return;
        assetBrowser.SelectFolder(primary); PasteAssets();
    }
}
