using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private ProjectWorkspace? selectedDependencyWorkspace;
    private long selectedDependencyNavigationVersion;
    private Package? CurrentSelectedDependencyReferencer()
        => workspace != null && ReferenceEquals(selectedDependencyWorkspace, workspace)
            && assetBrowser.SelectionVersion == selectedDependencyNavigationVersion
            && selectedDependencyPackage is { } package && selectedDependencyReference is { } dependency
            && ReferenceEquals(package.Session, workspace.Session) && package.Container.DirectDependencies.Contains(dependency)
                ? package : null;
    private bool CanAddSelectedPackageDependency()
    {
        if (!CanStartAssetEdit) return false;
        if (CurrentSelectedDependencyReferencer() is { } referencer) return IsCurrentEditableAssetPackage(referencer);
        return assetBrowser.CaptureNavigationScope().Locations.Select(assetBrowser.FindFolder)
            .Any(folder => folder is { Parent: null } && IsCurrentEditableAssetPackage(folder.Package));
    }
    private void RequestSelectedPackageDependency()
    {
        if (!CanAddSelectedPackageDependency()) return;
        if (CurrentSelectedDependencyReferencer() is { } reference) { RequestAddPackageDependency(reference); return; }
        var scope = assetBrowser.CaptureNavigationScope();
        if (scope.Locations.Length != 1 || assetBrowser.FindFolder(scope.Locations[0]) is not { } folder)
        { RequestAssetExploreMessage("Please select a single package."); return; }
        RequestAddPackageDependency(folder.Package);
    }
    private void DrawProjectDependencyCommand()
    {
        var selectedReference = CurrentSelectedDependencyReferencer();
        var selectedDependency = selectedDependencyReference;
        var command = CaptureProjectLocationCommand(CanAddSelectedPackageDependency, () =>
        {
            if (!ReferenceEquals(CurrentSelectedDependencyReferencer(), selectedReference)
                || selectedReference != null && !ReferenceEquals(selectedDependencyReference, selectedDependency)) return;
            RequestSelectedPackageDependency();
        });
        if (DrawNativeMenuItem(EditorLocalization.Widget("Add dependency...", "Menu"), "", false, command != null)) Execute(command!);
    }
}
