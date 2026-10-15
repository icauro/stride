using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private StandaloneDependencyAuthority? standaloneDependencyDeletion;
    private sealed class StandaloneDependencyAuthority(ProjectWorkspace workspace, EditorSessionHistory history,
        StandalonePackage package, DependencyRange? dependency, EditorAuthoringState<PackageMetadataDocument> owner)
    {
        internal readonly ProjectWorkspace Workspace = workspace;
        internal readonly EditorSessionHistory History = history;
        internal readonly StandalonePackage Package = package;
        internal readonly DependencyRange? Dependency = dependency;
        internal readonly EditorAuthoringState<PackageMetadataDocument> Owner = owner;
        internal readonly long Revision = owner.Document.Revision;
        internal readonly DependencyRange[] Declarations = package.DirectDependencies.ToArray();
        internal readonly (string, string, DependencyType, string)[] Values = package.DirectDependencies
            .Select(value => (value.Name, value.VersionRange?.ToString() ?? "", value.Type, value.MSBuildProject)).ToArray();
        private IDisposable? lease = owner.AcquireHistory();
        internal bool Current(EditorGame game) => ReferenceEquals(game.workspace, Workspace) && ReferenceEquals(game.sessionHistory, History)
            && ReferenceEquals(Package.Session, Workspace.Session) && !Package.Package.IsReadOnly
            && Package.Package.State >= PackageState.AssetsReady && Owner.IsPublished && !Owner.IsDisposed
            && ReferenceEquals(History.Registry.Find(Owner.Document.Path), Owner) && Owner.Document.Revision == Revision
            && Package.DirectDependencies.SequenceEqual(Declarations) && (Dependency == null || Package.DirectDependencies.Contains(Dependency))
            && Package.DirectDependencies.Select(value => (value.Name, value.VersionRange?.ToString() ?? "", value.Type, value.MSBuildProject)).SequenceEqual(Values);
        internal void Release() { lease?.Dispose(); lease = null; }
    }
    private void RequestStandaloneDependencyRemoval(StandalonePackage package, DependencyRange dependency)
    {
        if (workspace is not { } current || SessionAuthoringHistory is not { } history || !CanStartAssetEdit
            || package.Package.IsReadOnly || !ReferenceEquals(package.Session, current.Session)
            || package.Package.State < PackageState.AssetsReady || !package.DirectDependencies.Contains(dependency)) return;
        ValidatePendingPropertyStrings(); documents.CommitAll();
        PackageDependencyService().ValidateStandaloneRemoval(package, [dependency]);
        var owner = history.Registry.GetOrCreate(package.Package.FullPath.ToOSPath(), null,
            () => new PackageMetadataDocument(current, package.Package, history));
        ClearStandaloneDependencyRemoval();
        var run = new StandaloneDependencyAuthority(current, history, package, dependency, owner);
        standaloneDependencyDeletion = run;
        removingPackageDependency = null; dependencyToRemove = null; showPackageDependencyDelete = true;
    }
    private void CommitStandaloneDependencyRemoval(StandaloneDependencyAuthority run)
    {
        if (!run.Current(this)) { ClearStandaloneDependencyRemoval(); return; }
        if (!CanStartAssetEdit) return;
        ValidatePendingPropertyStrings();
        if (!run.Current(this)) { ClearStandaloneDependencyRemoval(); return; }
        using var capture = run.History.CaptureOperationsAtomic(() =>
            PackageDependencyService().RemoveStandaloneDependencies(run.Package, [run.Dependency!]));
        capture.Publish(run.History, "Delete dependency '" + run.Dependency!.Name + "'");
        selectedDependencyReference = null; selectedDependencyPackage = null;
        ClearAssetReferenceState(); ClearPackagePropertiesState(); StopScriptSemantics();
        closeCheckedDependencyDeletion = true; ClearStandaloneDependencyRemoval();
    }
    private void ClearStandaloneDependencyRemoval()
    {
        if (standaloneDependencyDeletion is not { } run) return;
        standaloneDependencyDeletion = null;
        try { run.Release(); }
        catch { if (!packageCommandCleanup.Contains(run.Release)) packageCommandCleanup.Add(run.Release); throw; }
    }
}
