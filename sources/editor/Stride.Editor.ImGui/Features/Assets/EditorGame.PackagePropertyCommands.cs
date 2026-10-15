using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private Package? SelectedPackagePropertiesTarget()
    {
        if (workspace == null) return null;
        var scope = assetBrowser.CaptureNavigationScope();
        var folders = scope.Locations.Select(assetBrowser.FindFolder).OfType<AssetBrowserFolder>().ToArray();
        if (!folders.Any(folder => folder.Parent != null || !folder.Package.IsReadOnly)) return null;
        var package = folders.FirstOrDefault()?.Package;
        return package != null && workspace.Session.Packages.Any(candidate => ReferenceEquals(candidate, package)) ? package : null;
    }

    private void RequestSelectedPackageProperties()
    {
        var expected = workspace;
        var scope = assetBrowser.CaptureNavigationScope();
        var version = assetBrowser.SelectionVersion;
        var package = SelectedPackagePropertiesTarget();
        if (expected == null || package == null) return;
        ValidatePendingPropertyStrings();
        FinishEntityRename(true);
        if (!ReferenceEquals(workspace, expected) || assetBrowser.SelectionVersion != version
            || !scope.SameScope(assetBrowser.CaptureNavigationScope())
            || !ReferenceEquals(SelectedPackagePropertiesTarget(), package))
            throw new InvalidOperationException("The selected package changed while finishing pending edits.");
        RequestPackageProperties(package);
    }
}
