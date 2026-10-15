using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal enum AssetBrowserCategoryKind { Local, External }

internal sealed class AssetBrowserCategory(ProjectWorkspace owner, AssetBrowserCategoryKind kind, string id, AssetBrowserFolder[] roots)
{
    internal ProjectWorkspace Owner { get; } = owner;
    internal AssetBrowserCategoryKind Kind { get; } = kind;
    internal string Id { get; } = id;
    internal IReadOnlyList<AssetBrowserFolder> Roots { get; } = roots;
    internal string Name => Kind == AssetBrowserCategoryKind.External ? EditorLocalization.Text("External packages")
        : Owner.Session.SolutionPath is { } path ? string.Format(EditorLocalization.Text("Solution '{0}'"), path.GetFileNameWithoutExtension())
        : EditorLocalization.Text("Local packages");
}

internal sealed record AssetBrowserLocation
{
    internal AssetBrowserLocation(AssetBrowserFolder folder) { Folder = folder; }
    internal AssetBrowserLocation(AssetBrowserCategory category) { Category = category; }
    internal AssetBrowserFolder? Folder { get; }
    internal AssetBrowserCategory? Category { get; }
    internal string Id => Folder?.Id ?? Category!.Id;
    internal string Name => Folder?.Name ?? Category!.Name;
}

internal sealed partial class AssetBrowserState
{
    private readonly List<AssetBrowserCategory> categories = [];
    private readonly HashSet<string> selectedCategoryIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> selectedLocationOrder = [];
    private readonly Dictionary<string, AssetBrowserLocation> locationModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (object Parent, int Count, AssetBrowserLocation[] Children)> locationChildren = new(StringComparer.OrdinalIgnoreCase);
    private string? primaryLocation;
    internal IReadOnlyList<AssetBrowserCategory> Categories => categories;
    internal int SelectedLocationCount => selectedFolderIds.Count + selectedCategoryIds.Count;
    internal AssetBrowserCategory? SelectedCategory => primaryLocation != null && selectedCategoryIds.Contains(primaryLocation) ? FindCategory(primaryLocation) : null;
    internal AssetBrowserCategory? FindCategory(string id) => categories.FirstOrDefault(category => string.Equals(category.Id, id, StringComparison.OrdinalIgnoreCase));
    internal bool IsCategorySelected(AssetBrowserCategory category) => selectedCategoryIds.Contains(category.Id);
    internal bool IsLocationSelected(AssetBrowserLocation location) => location.Folder is { } folder ? IsFolderSelected(folder) : IsCategorySelected(location.Category!);

    private void BindCategories(ProjectWorkspace? owner)
    {
        categories.Clear(); locationModels.Clear(); locationChildren.Clear(); selectedCategoryIds.Clear(); selectedLocationOrder.Clear(); primaryLocation = null;
        if (owner == null) return;
        var identity = Path.GetFullPath(owner.OpenPath);
        categories.Add(new(owner, AssetBrowserCategoryKind.Local, identity + "|@LocalPackages", packages.Where(root => !root.Package.IsReadOnly).ToArray()));
        categories.Add(new(owner, AssetBrowserCategoryKind.External, identity + "|@ExternalPackages", packages.Where(root => root.Package.IsReadOnly).ToArray()));
    }

    internal AssetBrowserLocation Location(AssetBrowserFolder folder)
    {
        if (!locationModels.TryGetValue(folder.Id, out var location) || !ReferenceEquals(location.Folder, folder))
            locationModels[folder.Id] = location = new(folder);
        return location;
    }
    internal AssetBrowserLocation Location(AssetBrowserCategory category)
    {
        if (!locationModels.TryGetValue(category.Id, out var location) || !ReferenceEquals(location.Category, category))
            locationModels[category.Id] = location = new(category);
        return location;
    }
    internal AssetBrowserLocation? FindLocation(string id)
        => FindFolder(id) is { } folder ? Location(folder) : FindCategory(id) is { } category ? Location(category) : null;
    internal AssetBrowserLocation? ParentLocation(AssetBrowserLocation location)
        => location.Folder is not { } folder ? null : folder.Parent is { } parent ? Location(parent)
            : categories.FirstOrDefault(category => category.Roots.Any(root => ReferenceEquals(root.Package, folder.Package))) is { } category ? Location(category) : null;
    internal IReadOnlyList<AssetBrowserLocation> LocationChildren(AssetBrowserLocation location)
    {
        object parent = (object?)location.Folder ?? location.Category!;
        IReadOnlyList<AssetBrowserFolder> children = location.Folder is { } folder ? folder.Children : location.Category!.Roots;
        if (!locationChildren.TryGetValue(location.Id, out var cached) || !ReferenceEquals(cached.Parent, parent) || cached.Count != children.Count)
            locationChildren[location.Id] = cached = (parent, children.Count, children.Select(Location).ToArray());
        return cached.Children;
    }

    private bool SameLocationAuthority(AssetBrowserLocation captured, AssetBrowserLocation current)
        => captured.Folder is { } folder ? current.Folder is { } actual && ReferenceEquals(folder.Package, actual.Package)
            && folder.RelativePath == actual.RelativePath && folder.IsCode == actual.IsCode
            : current.Category is { } category && ReferenceEquals(captured.Category!.Owner, category.Owner) && captured.Category.Kind == category.Kind;

    internal void SelectLocations(IEnumerable<AssetBrowserLocation> locations, AssetBrowserLocation primary, bool toggle)
    {
        var incoming = locations.Select(location => FindLocation(location.Id) is { } current && SameLocationAuthority(location, current) ? current : null)
            .OfType<AssetBrowserLocation>().DistinctBy(location => location.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        if (FindLocation(primary.Id) is not { } currentPrimary || !SameLocationAuthority(primary, currentPrimary)) return;
        List<string> order = toggle ? CaptureLocationOrder().ToList() : [];
        foreach (var location in incoming)
            if (!toggle || order.RemoveAll(id => string.Equals(id, location.Id, StringComparison.OrdinalIgnoreCase)) == 0) order.Add(location.Id);
        var nextPrimary = order.Contains(primary.Id, StringComparer.OrdinalIgnoreCase) ? primary.Id : order.LastOrDefault();
        if (!wholeWorkspaceScope && order.SequenceEqual(CaptureLocationOrder(), StringComparer.OrdinalIgnoreCase)
            && string.Equals(primaryLocation ?? SelectedFolder?.Id, nextPrimary, StringComparison.OrdinalIgnoreCase)) return;
        using var change = BeforeBrowserSelectionChange();
        InvalidateDirectoryScope();
        wholeWorkspaceScope = false; selectedFolderIds.Clear(); selectedCategoryIds.Clear(); selectedLocationOrder.Clear();
        foreach (var id in order)
        {
            if (FindFolder(id) != null) selectedFolderIds.Add(id); else if (FindCategory(id) != null) selectedCategoryIds.Add(id);
            selectedLocationOrder.Add(id);
        }
        primaryLocation = nextPrimary;
        SelectedFolder = nextPrimary == null ? null : FindFolder(nextPrimary);
        SelectedFolder ??= order.Select(FindFolder).LastOrDefault(folder => folder != null);
        HasSelectionInteraction = true; SelectionVersion++; ClearContentSelection(); RemoveSelectionOutsideFolder(); dirty = true;
    }

    private string[] CaptureLocationOrder()
        => selectedLocationOrder.Concat(selectedFolderIds).Concat(selectedCategoryIds)
            .Where(id => selectedFolderIds.Contains(id) || selectedCategoryIds.Contains(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private void RestoreLocationScope(AssetBrowserScopeSnapshot scope)
    {
        RestoreFolderScope(scope.Folders, scope.Primary, scope.EntireWorkspace);
        foreach (var id in scope.Categories) if (FindCategory(id) != null) selectedCategoryIds.Add(id);
        selectedLocationOrder.AddRange(scope.Locations.Where(id => selectedFolderIds.Contains(id) || selectedCategoryIds.Contains(id)).Distinct(StringComparer.OrdinalIgnoreCase));
        selectedLocationOrder.AddRange(selectedFolderIds.Concat(selectedCategoryIds).Where(id => !selectedLocationOrder.Contains(id, StringComparer.OrdinalIgnoreCase)));
        primaryLocation = scope.PrimaryLocation != null && (selectedFolderIds.Contains(scope.PrimaryLocation) || selectedCategoryIds.Contains(scope.PrimaryLocation))
            ? scope.PrimaryLocation : SelectedFolder?.Id ?? selectedLocationOrder.LastOrDefault();
    }

    private bool InSelectedCategory(AssetBrowserFolder folder, bool recursive)
    {
        if (!recursive || selectedCategoryIds.Count == 0) return false;
        for (var mount = folder; mount != null; mount = mount.Parent)
            if (mount.IsCode) return false;
        return categories.Any(category => selectedCategoryIds.Contains(category.Id) && category.Roots.Any(root => ReferenceEquals(root.Package, folder.Package)));
    }
}
