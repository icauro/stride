using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Packages;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal sealed partial class PackageDependencies
{
    private readonly HashSet<SolutionProject> restoringProjects = [];

    public void AddSuggested(SolutionProject project, PackageName suggested)
    {
        Check(project);
        if (project.DirectDependencies.Any(dependency => string.Equals(dependency.Name, suggested.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("This dependency already exists.");
        var dependency = new DependencyRange(suggested.Id, new PackageVersionRange(suggested.Version, true), DependencyType.Package);
        Owner(project).ChangeDependency(null, dependency, next => ValidateRemoval(project, next));
    }

    // The host dispatches every graph/publication mutation to its editor thread.
    // Restore itself uses the real MSBuild/NuGet service and never synthesizes a graph.
    public async Task RestoreAsync(SolutionProject project, LoggerResult log,
        Func<Task> saveAuthoredProject, Func<Action, Task> applyOnEditorThread,
        Func<UFile, Task<Package>> loadDependencyPackage, CancellationToken cancellationToken = default)
    {
        lock (restoringProjects)
            if (!restoringProjects.Add(project)) throw new InvalidOperationException("This project's dependencies are already being restored.");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await saveAuthoredProject();
            long expectedRevision = 0;
            ProjectDependencyRestoreArtifacts? artifacts = null;
            Dependency[]? previousDependencies = null;
            UFile[] packageFiles = [];
            await applyOnEditorThread(() =>
            {
                Check(project);
                var owner = Owner(project);
                owner.VerifySavedProject();
                expectedRevision = owner.AuthoringRevision;
                artifacts = ProjectDependencyRestoreArtifacts.Prepare(owner.Path);
            });
            Exception? restoreFailure = null;
            try { await VSProjectHelper.RestoreNugetPackages(log, project.FullPath.ToOSPath(), false, cancellationToken); }
            catch (Exception failure) { restoreFailure = failure; }
            finally { artifacts!.Complete(); }
            await applyOnEditorThread(() =>
            {
                Check(project);
                var owner = Owner(project);
                var state = history.Registry.Find(owner.Path) ?? throw new InvalidOperationException("The dependency project has no canonical owner.");
                try { history.PushOperation(artifacts!.CreateOperation(), state); }
                catch (Exception failure)
                {
                    try { artifacts!.Replay(false); }
                    catch (Exception recovery)
                    {
                        history.RetainFailedInitialOperation(artifacts!.CreateOperation(), state);
                        throw new AggregateException("Dependency restore outputs could not be recorded or restored.", failure, recovery);
                    }
                    throw;
                }
            });
            if (restoreFailure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(restoreFailure).Throw();
            cancellationToken.ThrowIfCancellationRequested();
            if (log.HasErrors) throw new InvalidOperationException("Package restore failed.");
            await applyOnEditorThread(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Check(project);
                var owner = Owner(project);
                if (owner.AuthoringRevision != expectedRevision) throw new InvalidOperationException("The project changed while restoring dependencies.");
                owner.VerifySavedProject();
                previousDependencies = project.FlattenedDependencies.ToArray();
                if (!File.Exists(artifacts!.AssetsPath))
                    throw new InvalidOperationException("Package restore did not produce a dependency lock file.");
                try
                {
                    PackageSession.UpdateDependencies(project, false, true);
                    packageFiles = project.FlattenedDependencies.Where(dependency => dependency.Type == DependencyType.Package
                            && (dependency.Package == null || dependency.Package.State < PackageState.AssetsReady))
                        .Select(dependency => PackageStore.Instance.GetPackageFileName(dependency.Name, new PackageVersionRange(dependency.Version)))
                        .Where(file => file != null && File.Exists(file.ToOSPath())).Cast<UFile>().Distinct().ToArray();
                }
                finally { ReplaceResolved(project, previousDependencies); }
            });
            var loadedPackages = new Dictionary<UFile, Package>();
            foreach (var file in packageFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                loadedPackages.Add(file, await loadDependencyPackage(file));
            }
            await applyOnEditorThread(() =>
            {
                cancellationToken.ThrowIfCancellationRequested(); Check(project);
                var owner = Owner(project);
                if (owner.AuthoringRevision != expectedRevision || !project.FlattenedDependencies.SequenceEqual(previousDependencies!))
                    throw new InvalidOperationException("The project changed while preparing dependencies.");
                owner.VerifySavedProject();
                var before = previousDependencies!;
                try
                {
                    if (!File.Exists(artifacts!.AssetsPath))
                        throw new InvalidOperationException("Package restore did not produce a dependency lock file.");
                    PackageSession.UpdateDependencies(project, false, true);
                    foreach (var dependency in project.FlattenedDependencies)
                    {
                        if (dependency.Package?.State >= PackageState.AssetsReady || dependency.Type != DependencyType.Package) continue;
                        var file = PackageStore.Instance.GetPackageFileName(dependency.Name, new PackageVersionRange(dependency.Version));
                        if (file == null || !File.Exists(file.ToOSPath())) continue; // Ordinary assembly-only NuGet packages have no .sdpkg.
                        if (!loadedPackages.TryGetValue(file, out var loaded)) throw new InvalidOperationException("The native dependency package was not prepared.");
                        if (!workspace.Session.Packages.Contains(loaded)) throw new InvalidOperationException("The dependency package was not published in this session.");
                        dependency.Package = loaded;
                    }
                    if (log.HasErrors) throw new InvalidOperationException("Dependency loading failed.");
                    if (owner.AuthoringRevision != expectedRevision) throw new InvalidOperationException("The project changed while loading dependencies.");
                    owner.VerifySavedProject();
                    var after = project.FlattenedDependencies.ToArray();
                    var participants = before.Concat(after).Select(dependency => dependency.Package).Where(package => package != null)
                        .Cast<Package>().Append(project.Package).Distinct().Select(package => history.Registry.GetOrCreate(package.FullPath.ToOSPath(), null,
                            () => new PackageMetadataDocument(workspace, package, history))).Cast<EditorAuthoringState>().ToList();
                    participants.Add(history.Registry.GetOrCreate(owner.Path, null, () => owner));
                    using var transaction = history.CreateTransaction("Update dependencies");
                    history.PushOperation(new ResolvedDependenciesOperation(project, owner, expectedRevision, before, after), participants.ToArray());
                }
                catch { ReplaceResolved(project, before); throw; }
            });
        }
        finally { lock (restoringProjects) restoringProjects.Remove(project); }
    }

    private static void ReplaceResolved(SolutionProject project, IReadOnlyList<Dependency> values)
    {
        project.FlattenedDependencies.Clear();
        foreach (var dependency in values) project.FlattenedDependencies.Add(dependency);
    }
    private sealed class ResolvedDependenciesOperation(SolutionProject project, ProjectBuildSettingsDocument owner,
        long revision, Dependency[] before, Dependency[] after) : Operation
    {
        protected override void Rollback() => Replay(false);
        protected override void Rollforward() => Replay(true);
        private void Replay(bool forward)
        {
            var expected = forward ? before : after;
            var target = forward ? after : before;
            if (owner.AuthoringRevision != revision || !project.FlattenedDependencies.SequenceEqual(expected))
                throw new InvalidOperationException("The resolved project dependencies changed.");
            try { ReplaceResolved(project, target); }
            catch { ReplaceResolved(project, expected); throw; }
        }
    }
}
