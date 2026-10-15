using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

/// <summary>Authored dependencies on real package/project models and the canonical project-file owner.</summary>
internal sealed partial class PackageDependencies(ProjectWorkspace workspace, EditorSessionHistory history,
    Func<IReadOnlyDictionary<AssetId, Asset>> getAuthoredAssets)
{
    public Package[] GetCandidates(PackageContainer referencer) => workspace.Session.Packages.Where(package =>
        !ReferenceEquals(package, referencer.Package) && !referencer.DirectDependencies.Any(dependency => Matches(package, dependency))
        && !DependsOn(package, referencer.Package, []) && (package.Container is SolutionProject || package.Meta.Version != null)).ToArray();
    public void Add(SolutionProject referencer, IReadOnlyList<Package> packages)
    {
        Check(referencer);
        if (packages.Count == 0) return;
        using var capture = history.CaptureOperationsAtomic(() =>
        {
            foreach (var package in packages)
                AddLoadedDependency(referencer, package);
        });
        try
        {
            using var transaction = history.CreateTransaction(packages.Count == 1 ? "Add dependency to package '" + System.IO.Path.GetFileNameWithoutExtension(packages[0].FullPath.ToOSPath()) + "'" : "Add dependencies");
            history.PushOperation(new CompositeOperation(capture.Entries.Select(entry => entry.Operation)), capture.Participants);
        }
        catch { capture.RestoreApplied(); throw; }
    }
    public void AddLoadedDependency(SolutionProject referencer, Package package)
    {
        Check(referencer);
        if (!GetCandidates(referencer).Contains(package)) throw new InvalidOperationException("This package cannot be added as a dependency.");
        var dependency = new DependencyRange(System.IO.Path.GetFileNameWithoutExtension(package.FullPath.ToOSPath()),
            package.Meta.Version == null ? null! : new PackageVersionRange(package.Meta.Version, true),
            package.Container is SolutionProject ? DependencyType.Project : DependencyType.Package)
        { MSBuildProject = (package.Container as SolutionProject)?.FullPath.ToOSPath()! };
        Owner(referencer).ChangeDependency(null, dependency, next => ValidateRemoval(referencer, next));
    }
    public bool Update(SolutionProject referencer, DependencyRange dependency, string version)
    {
        Check(referencer);
        if (dependency.Type != DependencyType.Package || !PackageVersionRange.TryParse(version, out var range))
            throw new ArgumentException("Invalid package version range.", nameof(version));
        if (Equals(range, dependency.VersionRange)) return false;
        var replacement = new DependencyRange(dependency.Name, range, dependency.Type) { MSBuildProject = dependency.MSBuildProject };
        Owner(referencer).ChangeDependency(dependency, replacement, next => ValidateRemoval(referencer, next)); return true;
    }
    public void Remove(SolutionProject referencer, DependencyRange dependency, Action<PackageDependencyRemovalScope>? validateCSharp = null,
        Func<PackageDependencyRemovalScope>? evaluateScope = null)
    {
        Check(referencer);
        Owner(referencer).ChangeDependency(dependency, null, next =>
        {
            ValidateRemoval(referencer, next);
            if (next.Any(candidate => candidate.Name == dependency.Name && candidate.Type == dependency.Type && candidate.MSBuildProject == dependency.MSBuildProject)) return;
            var current = referencer.DirectDependencies.Single(candidate => candidate.Name == dependency.Name && candidate.Type == dependency.Type && candidate.MSBuildProject == dependency.MSBuildProject);
            var scope = evaluateScope?.Invoke() ?? GetRemovalScope(referencer, current);
            ValidateAuthoredTypeUsage(referencer, scope);
            if (validateCSharp == null && (scope.AssemblyPaths.Length > 0 || scope.ProjectPaths.Length > 0))
                throw new InvalidOperationException("C# dependency usage must be verified before removing this dependency.");
            validateCSharp?.Invoke(scope);
        });
    }
    internal void RemoveMany(SolutionProject referencer, IReadOnlyCollection<DependencyRange> removing,
        Action<PackageDependencyRemovalScope> validateCSharp, Func<PackageDependencyRemovalScope> evaluateScope, IReadOnlySet<AssetId>? deletingAssets = null)
    {
        Check(referencer);
        Owner(referencer).RemoveDependencies(removing, next =>
        {
            ValidateRemoval(referencer, next, deletingAssets);
            if (removing.All(dependency => next.Any(candidate => candidate.Name == dependency.Name && candidate.Type == dependency.Type
                && candidate.MSBuildProject == dependency.MSBuildProject))) return;
            var scope = evaluateScope();
            ValidateAuthoredTypeUsage(referencer, scope, deletingAssets);
            validateCSharp(scope);
        });
    }

    private ProjectBuildSettingsDocument Owner(SolutionProject project) => history.Registry.GetOrCreate(project.FullPath.ToOSPath(), null,
        () => new ProjectBuildSettingsDocument(project, history)).Document;
    private void Check(PackageContainer project)
    {
        if (project.Package.IsReadOnly || !workspace.Session.LocalPackages.Contains(project.Package)) throw new InvalidOperationException("This package can't be edited.");
    }
    internal static bool RefersTo(Package package, DependencyRange dependency) => Matches(package, dependency);
    private static bool Matches(Package package, DependencyRange dependency)
    {
        if (dependency.Type == DependencyType.Project) return package.Container is SolutionProject project
            && !string.IsNullOrEmpty(dependency.MSBuildProject) && string.Equals(System.IO.Path.GetFullPath(project.FullPath.ToOSPath()),
                System.IO.Path.GetFullPath(dependency.MSBuildProject), StringComparison.OrdinalIgnoreCase);
        return string.Equals(dependency.Name, package.Meta.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(dependency.Name, System.IO.Path.GetFileNameWithoutExtension(package.FullPath.ToOSPath()), StringComparison.OrdinalIgnoreCase);
    }
    private bool DependsOn(Package candidate, Package target, HashSet<Package> visited)
    {
        if (!visited.Add(candidate)) return false;
        if (candidate.Container.FlattenedDependencies.Any(dependency => ReferenceEquals(dependency.Package, target))) return true;
        return candidate.Container.DirectDependencies.Any(dependency => workspace.Session.Packages.Where(package => Matches(package, dependency))
            .Any(package => ReferenceEquals(package, target) || DependsOn(package, target, visited)));
    }
    private void ValidateRemoval(PackageContainer referencer, IReadOnlyList<DependencyRange> next, IReadOnlySet<AssetId>? deletingAssets = null)
    {
        HashSet<Package> Scope(IEnumerable<DependencyRange> declarations)
        {
            var packages = new HashSet<Package> { referencer.Package };
            foreach (var dependency in declarations)
                foreach (var package in workspace.Session.Packages.Where(package => Matches(package, dependency)
                    && (dependency.Type == DependencyType.Project || dependency.VersionRange == null || package.Meta.Version != null && dependency.VersionRange.Contains(package.Meta.Version))))
                {
                    packages.Add(package);
                    foreach (var child in package.Container.FlattenedDependencies.Where(child => child.Package != null)) packages.Add(child.Package);
                }
            return packages;
        }
        var remaining = Scope(next);
        var lost = Scope(referencer.DirectDependencies).Except(remaining).ToArray();
        if (lost.Length == 0) return;
        var lostIds = lost.SelectMany(package => package.Assets).Select(item => item.Id).ToHashSet();
        var dependencies = workspace.Session.DependencyManager.CaptureDependencies(getAuthoredAssets());
        if (referencer.Package.Assets.Any(asset => deletingAssets?.Contains(asset.Id) != true && dependencies.GetValueOrDefault(asset.Id)?.LinksOut.Any(link =>
            (link.Type & ContentLinkType.Reference) != 0 && lostIds.Contains(link.Item.Id)) == true)
            || referencer.Package.RootAssets.Any(root => lostIds.Contains(root.Id)))
            throw new InvalidOperationException("This dependency is referenced by assets in the package.");
    }
}
