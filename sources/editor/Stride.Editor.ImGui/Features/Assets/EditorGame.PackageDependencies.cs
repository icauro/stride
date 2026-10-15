using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Stride.Core.Packages;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private SolutionProject? addingPackageDependency;
    private PackageDependencyPickerContext? packageDependencyPickerContext;
    private sealed record PackageDependencyCandidate(string Name, Package? Loaded, PackageName? Suggested);
    private PackageDependencyCandidate[] packageDependencyCandidates = [];
    private PackageDependencyCandidate? selectedPackageDependency;
    private SolutionProject? removingPackageDependency;
    private DependencyRange? dependencyToRemove;
    private DependencyRange? selectedDependencyReference;
    private Package? selectedDependencyPackage;
    private bool showPackageDependencyPicker, showPackageDependencyDelete;
    private PackageDependencies PackageDependencyService() => new(workspace!, SessionAuthoringHistory!, () =>
        SessionAuthoringHistory!.Registry.PublishedStates.Select(state => state.Document).OfType<IAssetReferenceDocument>()
            .ToDictionary(document => document.AuthoredAsset.Id, document => document.AuthoredAsset));
    private void RequestAddPackageDependency(Package package)
    {
        if (package.Container is not SolutionProject and not StandalonePackage || package.IsReadOnly
            || package.State < PackageState.AssetsReady || SessionAuthoringHistory == null || workspace == null
            || !ReferenceEquals(package.Session, workspace.Session)) return;
        if (IsAuthoringPipelineUpdating || IsPackageDependencyBusy || projectLoading != null || assetPreviewBuild != null || IsUserProjectBuilding
            || closeRequested || closePromptOpen || closingDocument != null) return;
        ValidatePendingPropertyStrings();
        if (package.Container is SolutionProject project)
        {
            ClearStandaloneDependencyAddition();
            var owner = GetProjectBuildSettingsDocument(project, SessionAuthoringHistory);
            owner.CommitEdits(); owner.ValidateProjectFileBaseline();
            packageDependencyPickerContext = new(workspace, SessionAuthoringHistory, project, owner, owner.AuthoringRevision);
            addingPackageDependency = project;
        }
        else
        {
            PrepareStandaloneDependencyAddition((StandalonePackage)package.Container);
            addingPackageDependency = null; packageDependencyPickerContext = null;
        }
        var loadedCandidates = PackageDependencyService().GetCandidates(package.Container);
        var candidates = loadedCandidates
            .Select(candidatePackage => new PackageDependencyCandidate(System.IO.Path.GetFileNameWithoutExtension(candidatePackage.FullPath.ToOSPath()), candidatePackage, null)).ToList();
        // These are the explicit suggestions registered by StrideDefaultAssetsPlugin
        // in WPF, at the running Stride build's actual package version.
        var version = new Stride.Core.PackageVersion(Stride.Assets.StridePackageUpgrader.CurrentVersion);
        foreach (var name in new[] { "Stride.Engine", "Stride.UI", "Stride.Particles", "Stride.Navigation", "Stride.Physics", "Stride.BepuPhysics", "Stride.Video", "Stride.Voxels", "Stride.SpriteStudio.Runtime" })
            if (!candidates.Any(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                && !workspace!.Session.Packages.Any(candidatePackage => string.Equals(candidatePackage.Meta.Name, name, StringComparison.OrdinalIgnoreCase))
                && !package.Container.DirectDependencies.Any(dependency => string.Equals(dependency.Name, name, StringComparison.OrdinalIgnoreCase)))
                candidates.Add(new(name, null, new PackageName(name, version)));
        if (candidates.Count == 0)
        {
            ClearStandaloneDependencyAddition(); addingPackageDependency = null; packageDependencyPickerContext = null;
            RequestAssetExploreMessage("There are no packages that can be added as dependencies to this package."); return;
        }
        packageDependencyCandidates = candidates.ToArray();
        selectedPackageDependency = null; closePackageDependencyPicker = false; showPackageDependencyPicker = true;
    }
    private void DrawPackageDependencyFolderActions(Package package)
    {
        if (DrawNativeMenuItem(EditorLocalization.Widget("Add dependency...", "Menu"), "", false, (package.Container is SolutionProject or StandalonePackage) && !package.IsReadOnly
            && package.State >= PackageState.AssetsReady && SessionAuthoringHistory != null && !IsAuthoringPipelineUpdating && !IsPackageDependencyBusy))
            Execute(() => RequestAddPackageDependency(package));
    }
    private void DrawPackageDependencyExplorer(Package package)
    {
        var container = package.Container;
        if (!assetFolderTree.Node("Dependencies", ImGuiTreeNodeFlags.SpanAvailWidth)) return;
        foreach (var dependency in container.DirectDependencies)
        {
            if (ReferenceEquals(selectedDependencyPackage, package) && selectedDependencyReference != null && selectedDependencyReference.Name == dependency.Name && selectedDependencyReference.Type == dependency.Type
                && selectedDependencyReference.MSBuildProject == dependency.MSBuildProject) selectedDependencyReference = dependency;
            Gui.PushID(dependency.Type + "/" + dependency.Name + "/" + dependency.MSBuildProject);
            if (assetFolderTree.Selectable(dependency.Name, ReferenceEquals(selectedDependencyPackage, package) && ReferenceEquals(selectedDependencyReference, dependency)))
            {
                selectedDependencyReference = dependency; selectedDependencyPackage = package; inspectedPackage = null;
                selectedDependencyWorkspace = workspace; selectedDependencyNavigationVersion = assetBrowser.SelectionVersion;
            }
            if (Gui.IsItemHovered() && Gui.IsMouseClicked(ImGuiMouseButton.Right))
            {
                selectedDependencyReference = dependency; selectedDependencyPackage = package; inspectedPackage = null;
                selectedDependencyWorkspace = workspace; selectedDependencyNavigationVersion = assetBrowser.SelectionVersion;
            }
            bool requestDelete = false;
            if (Gui.BeginPopupContextItem("Dependency actions"))
            {
                DrawPackageDependencyFolderActions(package);
                if (DrawNativeMenuItem(EditorLocalization.Widget("Delete", "Menu"), "", false, !package.IsReadOnly && package.State >= PackageState.AssetsReady
                    && CanStartAssetEdit)) requestDelete = true;
                Gui.EndPopup();
            }
            Gui.PopID();
            if (requestDelete) Execute(() =>
            {
                if (container is StandalonePackage standalone) RequestStandaloneDependencyRemoval(standalone, dependency);
                else if (container is SolutionProject project)
                { ClearStandaloneDependencyRemoval(); removingPackageDependency = project; dependencyToRemove = dependency; showPackageDependencyDelete = true; }
            });
        }
        Gui.TreePop();
    }
    private void DrawPackageDependencyDialogs()
    {
        DrawPackageDependencyPicker();
        var confirmationTitle = WpfMessageDialogTitle("DeleteDependencyConfirmation");
        if (showPackageDependencyDelete) { Gui.OpenPopup(confirmationTitle); showPackageDependencyDelete = false; }
        if (!Gui.BeginPopupModal(confirmationTitle, ImGuiWindowFlags.AlwaysAutoResize)) return;
        if (closeCheckedDependencyDeletion) { closeCheckedDependencyDeletion = false; Gui.CloseCurrentPopup(); Gui.EndPopup(); return; }
        Gui.TextUnformatted(EditorLocalization.Text("Are you sure you want to delete this dependency?", "Message"));
        Gui.BeginDisabled(IsPackageDependencyChecking || standaloneDependencyDeletion is { } run && (!run.Current(this) || !CanStartAssetEdit));
        var confirmDependencyDelete = Gui.Button(EditorLocalization.Widget("Delete", "Button"));
        Gui.EndDisabled();
        Gui.SameLine();
        var cancelDependencyDelete = Gui.Button(EditorLocalization.Widget("Cancel", "Button"));
        if (cancelDependencyDelete) Gui.CloseCurrentPopup();
        Gui.EndPopup();
        if (cancelDependencyDelete) Execute(() =>
        { packageDependencyRemovalCheck?.Cancellation.Cancel(); removingPackageDependency = null; dependencyToRemove = null; ClearStandaloneDependencyRemoval(); });
        else if (confirmDependencyDelete) Execute(() =>
        {
            if (standaloneDependencyDeletion is { } standalone) CommitStandaloneDependencyRemoval(standalone);
            else if (removingPackageDependency != null && dependencyToRemove != null)
                RequestCheckedPackageDependencyRemoval(removingPackageDependency, dependencyToRemove);
        });
    }
    private void ClearPackageDependencyState()
    {
        ClearStandaloneDependencyRemoval(); ClearStandaloneDependencyAddition(); closePackageDependencyPicker = false;
        ClearPackageDependencyRestoreState();
        ClearPackageDependencyRemovalCheckState();
        addingPackageDependency = removingPackageDependency = null;
        packageDependencyPickerContext = null;
        dependencyToRemove = selectedDependencyReference = null; selectedDependencyPackage = null; selectedDependencyWorkspace = null;
        selectedPackageDependency = null; packageDependencyCandidates = [];
        showPackageDependencyPicker = showPackageDependencyDelete = false;
    }
}
