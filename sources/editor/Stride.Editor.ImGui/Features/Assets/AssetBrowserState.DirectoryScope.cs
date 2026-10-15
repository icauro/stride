namespace Stride.Editor.ImGui;

internal sealed partial class AssetBrowserState
{
    private AssetBrowserFolder[] selectedDirectories = [];
    private readonly HashSet<string> selectedDirectoryIds = new(StringComparer.OrdinalIgnoreCase);
    private bool directoryScopeDirty = true;

    private void InvalidateDirectoryScope()
    { selectedDirectories = []; selectedDirectoryIds.Clear(); directoryScopeDirty = true; }
    private IReadOnlyList<AssetBrowserFolder> SelectedDirectories
    {
        get
        {
            if (!directoryScopeDirty) return selectedDirectories;
            selectedDirectories = selectedFolderIds.Select(FindFolder).OfType<AssetBrowserFolder>()
                .Select(folder => folder.Parent == null ? folder.Children.FirstOrDefault(child => !child.IsCode) : folder)
                .OfType<AssetBrowserFolder>().DistinctBy(folder => folder.Id, StringComparer.OrdinalIgnoreCase).ToArray();
            selectedDirectoryIds.Clear();
            foreach (var directory in selectedDirectories) selectedDirectoryIds.Add(directory.Id);
            directoryScopeDirty = false;
            return selectedDirectories;
        }
    }
    private bool IsSelectedDirectory(string id)
    {
        _ = SelectedDirectories;
        return selectedDirectoryIds.Contains(id);
    }
}
