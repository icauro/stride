namespace Stride.Editor.ImGui;

internal sealed record AssetBrowserScopeSnapshot(string[] Folders, string? Primary, bool EntireWorkspace, AssetBrowserDisplayMode DisplayMode)
{
    internal string[] Categories { get; init; } = [];
    internal string[] Locations { get; init; } = [];
    internal string? PrimaryLocation { get; init; }
    internal bool SameScope(AssetBrowserScopeSnapshot other)
        => EntireWorkspace == other.EntireWorkspace && DisplayMode == other.DisplayMode
            && string.Equals(Primary, other.Primary, StringComparison.OrdinalIgnoreCase)
            && Folders.SequenceEqual(other.Folders, StringComparer.OrdinalIgnoreCase)
            && Categories.SequenceEqual(other.Categories, StringComparer.OrdinalIgnoreCase)
            && Locations.SequenceEqual(other.Locations, StringComparer.OrdinalIgnoreCase)
            && string.Equals(PrimaryLocation, other.PrimaryLocation, StringComparison.OrdinalIgnoreCase);
}

internal sealed partial class AssetBrowserState
{
    internal AssetBrowserScopeSnapshot CaptureNavigationScope()
        => new(selectedFolderIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray(), SelectedFolder?.Id, wholeWorkspaceScope, displayMode)
        {
            Categories = selectedCategoryIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
            Locations = CaptureLocationOrder(), PrimaryLocation = primaryLocation ?? SelectedFolder?.Id,
        };

    internal void RestoreNavigationScope(AssetBrowserScopeSnapshot scope)
    {
        if (CaptureNavigationScope().SameScope(scope)) return;
        using var change = BeforeBrowserSelectionChange();
        RestoreLocationScope(scope);
        displayMode = scope.DisplayMode; includeDescendants = displayMode == AssetBrowserDisplayMode.RecursiveAssets;
        dirty = true; SelectionVersion++;
    }
}
