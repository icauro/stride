using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed class AssetBrowserFolder(string id, string name, AssetBrowserFolder? parent, Package package, string relativePath, bool isCode = false)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public AssetBrowserFolder? Parent { get; } = parent;
    public Package Package { get; } = package;
    public string RelativePath { get; } = relativePath;
    public bool IsCode { get; } = isCode;
    public IReadOnlyList<string> SourceFolders { get; internal set; } = [];
    public List<AssetBrowserFolder> Children { get; } = [];
}

internal sealed partial class AssetBrowserState
{
    private readonly List<AssetBrowserFolder> packages = [];
    private readonly Dictionary<AssetId, AssetBrowserFolder> assetFolders = [];
    private bool includeDescendants;
    private bool wholeWorkspaceScope;
    private readonly HashSet<string> selectedFolderIds = new(StringComparer.OrdinalIgnoreCase);
    internal bool IsFolderSelected(AssetBrowserFolder folder) => selectedFolderIds.Contains(folder.Id);
    public IReadOnlyList<AssetBrowserFolder> Packages => packages;
    public AssetBrowserFolder? SelectedFolder { get; private set; }
    public bool IncludeDescendants
    {
        get => includeDescendants;
        set
        {
            if (includeDescendants == value) return;
            using var change = source != null && selected.Any(id => source.FirstOrDefault(asset => asset.Id == id) is not { } entry || !InSelectedFolder(entry, value))
                ? BeforeBrowserSelectionChange() : null;
            includeDescendants = value;
            if (value) displayMode = AssetBrowserDisplayMode.RecursiveAssets;
            else if (displayMode == AssetBrowserDisplayMode.RecursiveAssets) displayMode = AssetBrowserDisplayMode.AssetsOnly;
            RemoveSelectionOutsideFolder(); dirty = true;
        }
    }

    private void BindFolders(ProjectWorkspace? workspace)
    {
        packages.Clear(); assetFolders.Clear(); selectedFolderIds.Clear(); wholeWorkspaceScope = false; SelectedFolder = null; InvalidateDirectoryScope();
        BindCategories(null);
        if (workspace == null) return;
        var roots = new Dictionary<Package, AssetBrowserFolder>();
        var assets = source?.Select(asset => asset.Id).ToHashSet() ?? [];
        foreach (var package in workspace.Session.Packages.OrderBy(PackageLocationName, StringComparer.OrdinalIgnoreCase))
        {
            var root = new AssetBrowserFolder(Path.GetFullPath(package.FullPath.ToOSPath()), PackageLocationName(package), null, package, "");
            roots.Add(package, root); packages.Add(root);
            AddMounts(root);
            var assetsMount = root.Children.First(folder => folder.Name == "Assets");
            foreach (var path in package.ExplicitFolders)
                GetOrCreateFolder(assetsMount, path.ToString());
        }
        foreach (var item in workspace.AssetItems)
        {
            if (!assets.Contains(item.Id)) continue;
            if (!roots.TryGetValue(item.Package, out var root))
            {
                root = new AssetBrowserFolder(Path.GetFullPath(item.Package.FullPath.ToOSPath()), PackageLocationName(item.Package), null, item.Package, "");
                roots.Add(item.Package, root); packages.Add(root); AddMounts(root);
            }
            var code = item.Asset is IProjectAsset && item.Package.Container is SolutionProject;
            var mount = root.Children.First(folder => folder.IsCode == code);
            if (item.SourceFolder != null)
                mount.SourceFolders = mount.SourceFolders.Append(item.SourceFolder.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var location = item.UnqualifiedUrl.ToString().Replace('\\', '/');
            var slash = location.LastIndexOf('/');
            var folder = slash >= 0 ? GetOrCreateFolder(mount, location[..slash]) : mount;
            assetFolders[item.Id] = folder;
        }
        foreach (var root in packages) SortFolders(root);
        var selectedPackage = (workspace.Package is { } active ? roots.GetValueOrDefault(active) : null) ?? packages.FirstOrDefault();
        SelectedFolder = selectedPackage?.Children.FirstOrDefault(folder => folder.Name == "Assets") ?? selectedPackage;
        if (SelectedFolder != null) selectedFolderIds.Add(SelectedFolder.Id);
        BindCategories(workspace); primaryLocation = SelectedFolder?.Id;
    }

    private static string PackageLocationName(Package package)
        => package.Container is SolutionProject project ? project.Name : Path.GetFileNameWithoutExtension(package.FullPath.ToOSPath());

    private static void AddMounts(AssetBrowserFolder root)
    {
        var assetsMount = new AssetBrowserFolder(root.Id + "/@Assets", "Assets", root, root.Package, "")
        {
            SourceFolders = root.Package.AssetFolders.Select(folder => folder.Path.ToString()).ToArray()
        };
        root.Children.Add(assetsMount);
        if (root.Package.Container is SolutionProject)
            root.Children.Add(new AssetBrowserFolder(root.Id + "/@Code", "Code", root, root.Package, "", true));
    }

    private static void SortFolders(AssetBrowserFolder folder)
    {
        folder.Children.Sort((left, right) => NaturalCompare(left.Name, right.Name));
        foreach (var child in folder.Children) SortFolders(child);
    }

    private static AssetBrowserFolder GetOrCreateFolder(AssetBrowserFolder root, string path)
    {
        var folder = root;
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var child = folder.Children.FirstOrDefault(child => string.Equals(child.Name, part, StringComparison.OrdinalIgnoreCase));
            if (child == null)
            {
                child = new AssetBrowserFolder(folder.Id + "/" + part, part, folder, folder.Package, folder.RelativePath.Length == 0 ? part : folder.RelativePath + "/" + part, folder.IsCode);
                folder.Children.Add(child);
            }
            folder = child;
        }
        return folder;
    }

    internal void AddProjectFolders(Package package, IEnumerable<string> paths)
    {
        var root = packages.FirstOrDefault(folder => ReferenceEquals(folder.Package, package));
        var code = root?.Children.FirstOrDefault(folder => folder.IsCode);
        if (code == null) return;
        foreach (var raw in paths)
        {
            var path = raw.Replace('\\', '/').TrimEnd('/');
            if (path.Length == 0 || Path.IsPathRooted(path) || path.Split('/').Any(part => part is "" or "." or "..")) continue;
            GetOrCreateFolder(code, path);
        }
        SortFolders(code);
    }
    public void SelectFolder(AssetBrowserFolder folder) => SelectFolders([folder], folder, false);
    internal void SelectFolders(IEnumerable<AssetBrowserFolder> folders, AssetBrowserFolder primary, bool toggle)
        => SelectLocations(folders.Select(Location), Location(primary), toggle);
    private void RestoreFolderScope(string[] ids, string? primary, bool entireWorkspace = false)
    {
        wholeWorkspaceScope = entireWorkspace; InvalidateDirectoryScope();
        selectedCategoryIds.Clear(); selectedLocationOrder.Clear(); primaryLocation = null;
        selectedFolderIds.Clear();
        foreach (var id in ids) if (FindFolder(id) != null) selectedFolderIds.Add(id);
        SelectedFolder = primary == null ? null : FindFolder(primary);
        if (SelectedFolder == null) SelectedFolder = selectedFolderIds.Select(FindFolder).FirstOrDefault(folder => folder != null);
        if (SelectedFolder != null && selectedFolderIds.Count == 0) selectedFolderIds.Add(SelectedFolder.Id);
        primaryLocation = SelectedFolder?.Id;
    }
    private bool InSelectedFolder(AssetEntry asset, bool? descendants = null)
    {
        if (!assetFolders.TryGetValue(asset.Id, out var folder)) return false;
        if (InSelectedCategory(folder, descendants ?? includeDescendants)) return true;
        if (selectedFolderIds.Count == 0) return wholeWorkspaceScope;
        if (!(descendants ?? includeDescendants)) return IsSelectedDirectory(folder.Id);
        for (; folder != null; folder = folder.Parent) if (IsSelectedDirectory(folder.Id)) return true;
        return false;
    }

    private void RemoveSelectionOutsideFolder()
    {
        if (source == null) return;
        var count = selected.Count;
        var removed = selected.Where(id => source.FirstOrDefault(asset => asset.Id == id) is not { } asset || !InSelectedFolder(asset)).ToArray();
        using var change = removed.Length > 0 ? BeforeBrowserSelectionChange() : null;
        selected.ExceptWith(removed);
        if (Primary is { } primary && !selected.Contains(primary)) Primary = selected.FirstOrDefault();
        if (selected.Count == 0) Primary = null;
        if (anchor is { } start && !selected.Contains(start)) anchor = null;
        if (count != selected.Count) SelectionVersion++;
    }

    public void RefreshFolders(ProjectWorkspace workspace)
    {
        var scope = CaptureNavigationScope();
        BindFolders(workspace);
        RestoreLocationScope(scope);
        dirty = true;
    }

    public AssetBrowserFolder? FindFolder(string id)
    {
        foreach (var root in packages)
        {
            var found = Find(root);
            if (found != null) return found;
        }
        return null;
        AssetBrowserFolder? Find(AssetBrowserFolder folder)
        {
            if (string.Equals(folder.Id, id, StringComparison.OrdinalIgnoreCase)) return folder;
            foreach (var child in folder.Children) { var found = Find(child); if (found != null) return found; }
            return null;
        }
    }

    private void ExpandSelectionScope()
    {
        if (source == null || selected.Count == 0 || source.Where(asset => selected.Contains(asset.Id)).All(asset => InSelectedFolder(asset))) return;
        selectedCategoryIds.Clear(); selectedLocationOrder.Clear(); primaryLocation = null;
        AssetBrowserFolder? common = null;
        var first = true;
        foreach (var id in selected)
        {
            if (!assetFolders.TryGetValue(id, out var folder)) continue;
            if (first) { common = folder; first = false; continue; }
            while (common != null)
            {
                var ancestor = folder;
                while (ancestor != null && !ReferenceEquals(ancestor, common)) ancestor = ancestor.Parent;
                if (ancestor != null) break;
                common = common.Parent;
            }
        }
        SelectedFolder = common; wholeWorkspaceScope = common == null; selectedFolderIds.Clear();
        if (common?.Parent == null && common != null)
        {
            // Automatic mixed Assets/Code reveal keeps the actual mounts explicit;
            // a native package selection represents only its AssetMountPoint.
            foreach (var id in selected)
            {
                if (!assetFolders.TryGetValue(id, out var directory)) continue;
                while (directory.Parent?.Parent != null) directory = directory.Parent;
                selectedFolderIds.Add(directory.Id);
            }
            SelectedFolder = Primary is { } primary && assetFolders.TryGetValue(primary, out var primaryFolder)
                ? primaryFolder : selectedFolderIds.Select(FindFolder).FirstOrDefault();
        }
        else if (common != null) selectedFolderIds.Add(common.Id);
        primaryLocation = SelectedFolder?.Id;
        if (primaryLocation != null && !selectedFolderIds.Contains(primaryLocation))
        {
            var directory = SelectedFolder!;
            while (directory.Parent?.Parent != null) directory = directory.Parent;
            SelectedFolder = directory; primaryLocation = directory.Id;
        }
        InvalidateDirectoryScope();
        includeDescendants = true;
    }
}
