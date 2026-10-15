using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private ProjectLocationRemoval? projectLocationRemoval;
    private bool openProjectLocationRemoval;
    private bool IsProjectLocationRemovalPending => projectLocationRemoval != null;
    private sealed partial class ProjectLocationRemoval(ProjectWorkspace workspace, PackageContainer[] containers,
        WorkspaceMembershipOwner membership, long selectionVersion)
    {
        internal readonly ProjectWorkspace Workspace = workspace;
        internal readonly PackageContainer[] Containers = containers;
        internal readonly PackageContainer[] SessionContainers = workspace.Session.Projects.ToArray();
        internal readonly WorkspaceMembershipOwner Membership = membership;
        internal readonly long MembershipRevision = membership.Revision, SelectionVersion = selectionVersion;
        internal readonly Dictionary<EditorAuthoringState, IDisposable> Leases = [];
        internal readonly Dictionary<EditorAuthoringState, long> Revisions = [];
        internal readonly Dictionary<PackageContainer, (string Name, string Path, string Version, DependencyType Type)[]> Dependencies
            = workspace.Session.Projects.ToDictionary(container => container, container => container.DirectDependencies
                .Select(dependency => (dependency.Name, dependency.MSBuildProject, dependency.VersionRange?.ToString() ?? "", dependency.Type)).ToArray());
        internal SolutionProject? RemainingProject;
        internal bool Switching, Confirmed;
        internal int Confirmation;
        internal string? Error;
        internal void Release()
        { foreach (var pair in Leases.ToArray()) { pair.Value.Dispose(); Leases.Remove(pair.Key); } }
    }

    private PackageContainer[] ExplorerRemovalPackages()
    {
        if (!CanStartAssetEdit || projectLocationRemoval != null || workspace == null) return [];
        var scope = assetBrowser.CaptureNavigationScope();
        if (scope.Categories.Length != 0 || scope.Locations.Length == 0) return [];
        var roots = scope.Locations.Select(assetBrowser.FindLocation).ToArray();
        if (roots.Any(location => location?.Folder is not { } folder || !IsCurrentEditableAssetPackage(folder.Package)
            || folder.Parent != null && !CanCutAssetFolder(folder))) return [];
        var containers = roots.Where(location => location!.Folder!.Parent == null).Select(location => location!.Folder!.Package.Container).Distinct().ToArray();
        if (containers.Length == 0) return [];
        if (containers.Any(container => !workspace.Session.Projects.Contains(container))) return [];
        if (workspace.Session.Projects.Except(containers).Any(container => container.DirectDependencies.Any(dependency =>
            containers.Any(target => PackageDependencies.RefersTo(target.Package, dependency))) && (container.Package.IsReadOnly || container is not SolutionProject and not StandalonePackage))) return [];
        return containers;
    }

    private bool CanRemoveExplorerPackages => ExplorerRemovalPackages().Length != 0;
    private void RequestExplorerPackageRemoval()
    {
        var selected = ExplorerRemovalPackages();
        if (selected.Length == 0 || workspace is not { } current || SessionAuthoringHistory is not { } history) return;
        ValidatePendingPropertyStrings(); FinishEntityRename(true); documents.CommitAll();
        if (!ReferenceEquals(workspace, current) || !ExplorerRemovalPackages().SequenceEqual(selected)) return;
        var membership = GetWorkspaceRemovalMembership(selected);
        var run = new ProjectLocationRemoval(current, selected, membership, assetBrowser.SelectionVersion);
        var ids = selected.SelectMany(container => container.Package.Assets).Select(item => item.Id).ToHashSet();
        var paths = selected.SelectMany(container => container is SolutionProject project
            ? new[] { container.Package.FullPath.ToOSPath(), container.Package.FullPath.ToOSPath() + ".user", project.FullPath.ToOSPath() }
            : new[] { container.Package.FullPath.ToOSPath(), container.Package.FullPath.ToOSPath() + ".user" })
            .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var state in history.Registry.PublishedStates.Where(state => ReferenceEquals(state.Document, membership.Document)
                || state.AssetId is { } id && ids.Contains(id) || paths.Contains(state.CanonicalPath)).ToArray())
            {
                run.Leases.Add(state, state.AcquireHistory());
                run.Revisions.Add(state, ProjectRemovalDocumentRevision(state.Document));
            }
        }
        catch { RetireProjectLocationRemoval(run); throw; }
        projectLocationRemoval = run;
        run.MixedFolders = assetBrowser.CaptureNavigationScope().Locations.Select(assetBrowser.FindLocation)
            .Where(location => location?.Folder?.Parent != null).Select(location => location!.Folder!).ToArray();
        try
        {
            if (run.MixedFolders.Length != 0) BeginMixedProjectFolderDeletion(run);
            else openProjectLocationRemoval = true;
        }
        catch { ClearProjectLocationRemoval(); throw; }
    }

    private static long ProjectRemovalDocumentRevision(IEditorDocument document) => document switch
    {
        IAssetTagDocument asset => asset.EditRevision,
        PackageMetadataDocument package => package.Revision,
        PackageSettingsDocument settings => settings.AuthoringRevision,
        ProjectBuildSettingsDocument project => project.AuthoringRevision,
        SolutionMembershipDocument solution => solution.Revision,
        _ => 0,
    };

    private bool CurrentProjectLocationRemoval(ProjectLocationRemoval run)
        => ReferenceEquals(workspace, run.Workspace) && run.Membership.IsCurrent
            && run.Membership.Revision == run.MembershipRevision && assetBrowser.SelectionVersion == run.SelectionVersion
            && run.Workspace.Session.Projects.SequenceEqual(run.SessionContainers)
            && CurrentMixedProjectFolderDeletion(run)
            && run.Containers.All(container => ReferenceEquals(container.Session, run.Workspace.Session) && !container.Package.IsReadOnly)
            && run.MixedFolders.All(folder => assetBrowser.FindFolder(folder.Id) is { } current
                && ReferenceEquals(current.Package, folder.Package) && current.RelativePath == folder.RelativePath && current.IsCode == folder.IsCode)
            && run.Dependencies.All(pair => pair.Key.DirectDependencies.Select(dependency =>
                (dependency.Name, dependency.MSBuildProject, dependency.VersionRange?.ToString() ?? "", dependency.Type)).SequenceEqual(pair.Value))
            && run.Leases.Keys.All(state => !state.IsDisposed && state.IsPublished && ReferenceEquals(state.Registry.Find(state.CanonicalPath), state)
                && ProjectRemovalDocumentRevision(state.Document) == run.Revisions[state]);

    private void DrawProjectLocationRemovalDialog()
    {
        var title = WpfMessageDialogTitle("DeleteProjectLocations");
        if (openProjectLocationRemoval) { Gui.OpenPopup(title); openProjectLocationRemoval = false; }
        if (!Gui.BeginPopupModal(title, ImGuiWindowFlags.AlwaysAutoResize)) return;
        Action? action = null;
        try
        {
            if (projectLocationRemoval is not { } run || !CurrentProjectLocationRemoval(run))
            { action = ClearProjectLocationRemoval; Gui.CloseCurrentPopup(); }
            else
            {
                var hasPackages = run.Containers.Any(container => container is not SolutionProject);
                var projectPhase = !hasPackages || run.Confirmation > 0;
                DrawSelectableText("##RemoveProjectLocations", EditorLocalization.Text(projectPhase
                    ? "Are you sure you want to delete these projects?"
                    : "Are you sure you want to delete this package? The package files will remain on the disk.", "Message"));
                if (run.Error != null) DrawSelectableText("##RemoveProjectLocationsError", run.Error);
                if (DrawNativeButton(EditorLocalization.Widget("Delete", "Button")))
                {
                    if (!projectPhase && run.Containers.Any(container => container is SolutionProject)) run.Confirmation++;
                    else { Gui.CloseCurrentPopup(); action = () => { if (CurrentProjectLocationRemoval(run)) { run.Confirmed = true; BeginProjectLocationRemoval(run); } else ClearProjectLocationRemoval(); }; }
                }
                Gui.SameLine();
                if (DrawNativeButton(EditorLocalization.Widget("Cancel", "Button"))) { action = ClearProjectLocationRemoval; Gui.CloseCurrentPopup(); }
            }
        }
        finally { Gui.EndPopup(); }
        if (action != null) Execute(action);
    }

    private void BeginProjectLocationRemoval(ProjectLocationRemoval run)
    {
        if (!ReferenceEquals(projectLocationRemoval, run) || !CurrentProjectLocationRemoval(run)) { ClearProjectLocationRemoval(); return; }
        if (run.Containers.Any(container => ReferenceEquals(container.Package, run.Workspace.Package)
            || ReferenceEquals(container, run.Workspace.Session.CurrentProject)))
        {
            run.RemainingProject = run.Workspace.Projects.FirstOrDefault(project => !run.Containers.Contains(project)
                && !project.Package.IsReadOnly && project.Package.State >= PackageState.AssetsReady);
            if (run.RemainingProject == null)
            {
                run.EmptyTransition = new(this, run.Workspace);
                run.EmptyTransition.RetireViews(); return;
            }
            try { StartActiveProjectSwitch(run.RemainingProject); run.Switching = true; }
            catch { run.Confirmed = false; openProjectLocationRemoval = true; throw; }
        }
        else PrepareProjectRemovalDependencyChecks(run);
    }

    private void PumpProjectLocationRemoval()
    {
        PumpEmptyWorkspaceRuntime();
        if (projectLocationRemoval is not { } run) return;
        if (!run.Confirmed)
        {
            if (run.MixedReady) { run.MixedReady = false; openProjectLocationRemoval = true; }
            return;
        }
        if (run.EmptyTransition is { Applied: false } empty && !run.EmptyReady)
        {
            if (empty.Barrier?.IsCompleted != true) return;
            Execute(() =>
            {
                empty.Barrier.GetAwaiter().GetResult();
                if (!CurrentProjectLocationRemoval(run)) { ClearProjectLocationRemoval(); return; }
                run.EmptyReady = true; PrepareProjectRemovalDependencyChecks(run);
            });
        }
        else if (run.Switching)
        {
            if (IsActiveProjectSwitching) return;
            if (!CurrentProjectLocationRemoval(run) || !ReferenceEquals(run.Workspace.Session.CurrentProject, run.RemainingProject)
                || !ReferenceEquals(run.Workspace.Package, run.RemainingProject?.Package)) { ClearProjectLocationRemoval(); return; }
            run.Switching = false;
            Execute(() => PrepareProjectRemovalDependencyChecks(run));
        }
        else PumpProjectRemovalDependencyChecks(run);
    }

    private void SynchronizeRemovedPackageViews(EditorAuthoringState[] states, bool removing)
    {
        var views = states.Select(state => state.Document).OfType<IAssetReferenceDocument>()
            .Where(document => removing ? !deletedDocuments.ContainsKey(document) : deletedDocuments.ContainsKey(document)).ToArray();
        if (views.Length != 0) SetDeletedAssetDocuments(views, removing);
    }

    private void CommitProjectLocationRemoval(ProjectLocationRemoval run)
    {
        if (!CurrentProjectLocationRemoval(run)) { ClearProjectLocationRemoval(); return; }
        ValidatePendingPropertyStrings();
        if (!CurrentProjectLocationRemoval(run)) { ClearProjectLocationRemoval(); return; }
        try
        {
            using var capture = SessionAuthoringHistory!.CaptureOperationsAtomic(() =>
            {
            ApplyProjectRemovalDependencies(run);
            if (run.MixedFolders.Length != 0) CommitAssetContentDeletion(run.MixedClearReferences);
            if (run.EmptyTransition is { } empty)
            {
                SessionAuthoringHistory.PushOperation(new EmptyWorkspaceActivationOperation(empty),
                    SessionAuthoringHistory.Registry.Find(run.Membership.Document.Path)!);
                empty.Change(true);
            }
            run.Membership.Remove(run.Containers, () =>
            {
                assetBrowser.RefreshEntries(run.Workspace); ClearAssetReferenceState();
                ClearPackagePropertiesState(); StopScriptSemantics();
            }, SynchronizeRemovedPackageViews);
            });
            capture.Publish(SessionAuthoringHistory, "Delete selected solution items");
            if (run.EmptyTransition is { } emptyTransition && SessionAuthoringHistory.Stack.NextUndoTransaction is { } transaction)
                emptyWorkspaceTransactions[transaction.Id] = emptyTransition;
            if (run.MixedFolders.Length != 0)
            { FinishCommittedAssetDeletion(false); CompleteAssetDeletionContext(true); run.MixedFinished = true; }
            ClearProjectLocationRemoval();
        }
        catch
        {
            // Actual failed capture retains its journal. Release the UI-only pins;
            // retry/compensation continues under that canonical history owner.
            ClearProjectLocationRemoval(); throw;
        }
    }

    private void RetireProjectLocationRemoval(ProjectLocationRemoval run)
    {
        try { run.ReleaseAll(); }
        catch { if (!packageCommandCleanup.Contains(run.ReleaseAll)) packageCommandCleanup.Add(run.ReleaseAll); throw; }
    }
    private void ClearProjectLocationRemoval()
    {
        openProjectLocationRemoval = false;
        if (projectLocationRemoval is not { } run) return;
        run.DependencyCancellation?.Cancel();
        if (run.DependencyTask is { } task)
        {
            // Teardown uses the same actual worker; its captured sources/leases cannot
            // be disposed while Roslyn is still reading them.
            try { task.GetAwaiter().GetResult(); } catch { /* The worker terminal state is observed without publication. */ }
        }
        projectLocationRemoval = null;
        if (run.EmptyTransition is { Applied: false } empty && empty.Current(false) && empty.Barrier?.IsCompletedSuccessfully == true
            && !RecoveryTransitionBusy && !closeRequested && !editorExtensionsStopping)
            if (!pendingEmptyViewRestores.Contains(empty)) pendingEmptyViewRestores.Add(empty);
        if (run.MixedFolders.Length != 0 && !run.MixedFinished) CancelAssetDeletion();
        RetireProjectLocationRemoval(run);
    }
}
