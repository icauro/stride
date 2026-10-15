namespace Stride.Editor.ImGui;

internal sealed partial class AssetBrowserState
{
    internal void RefreshPackageRename(ProjectWorkspace workspace, string previous, string next)
    {
        string? Remap(string? id) => id != null && (string.Equals(id, previous, StringComparison.OrdinalIgnoreCase)
            || id.StartsWith(previous + "/", StringComparison.OrdinalIgnoreCase) || id.StartsWith(previous + "|@", StringComparison.OrdinalIgnoreCase))
                ? next + id[previous.Length..] : id;
        var scope = CaptureNavigationScope();
        var assets = CaptureSelection(); var primaryAsset = Primary;
        var folders = CaptureContentFolderSelection(); var primaryFolder = PrimaryContentFolder;
        var order = CaptureContentSelectionOrder();
        var remapped = scope with
        {
            Folders = scope.Folders.Select(id => Remap(id)!).ToArray(), Primary = Remap(scope.Primary),
            Categories = scope.Categories.Select(id => Remap(id)!).ToArray(),
            Locations = scope.Locations.Select(id => Remap(id)!).ToArray(), PrimaryLocation = Remap(scope.PrimaryLocation),
        };
        RefreshFolders(workspace); RestoreNavigationScope(remapped);
        RestoreSelection(assets, primaryAsset);
        RestoreContentFolderSelection(folders.Select(id => Remap(id)!), Remap(primaryFolder));
        RestoreContentSelectionOrder(order.Select(id => id.StartsWith("folder/", StringComparison.Ordinal)
            ? "folder/" + Remap(id[7..]) : id));
        dirty = true;
    }
}
