using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Packages;

namespace Stride.Editor.ImGui;

internal sealed partial class PackageDependencies
{
    internal void ValidateStandaloneRemoval(StandalonePackage referencer, IReadOnlyCollection<DependencyRange> removing,
        IReadOnlySet<AssetId>? deletingAssets = null)
    {
        if (referencer.Package.IsReadOnly || !ReferenceEquals(referencer.Session, workspace.Session)
            || !workspace.Session.LocalPackages.Contains(referencer.Package) || referencer.Package.State < PackageState.AssetsReady)
            throw new InvalidOperationException("This package can't be edited.");
        if (removing.Count == 0 || removing.Any(dependency => !referencer.DirectDependencies.Contains(dependency)))
            throw new InvalidOperationException("The dependencies changed.");
        var next = referencer.DirectDependencies.Except(removing).ToArray();
        ValidateRemoval(referencer, next, deletingAssets);
        HashSet<Package> Reach(IEnumerable<DependencyRange> declarations)
        {
            var result = new HashSet<Package>();
            var pending = new Stack<Package>(declarations.SelectMany(dependency => workspace.Session.Packages.Where(package =>
                Matches(package, dependency) && (dependency.Type == DependencyType.Project || dependency.VersionRange == null
                    || package.Meta.Version != null && dependency.VersionRange.Contains(package.Meta.Version)))));
            while (pending.TryPop(out var package))
                if (result.Add(package))
                    foreach (var dependency in package.Container.FlattenedDependencies)
                        if (dependency.Package is { } child) pending.Push(child);
            return result;
        }
        var lost = Reach(referencer.DirectDependencies).Except(Reach(next)).ToArray();
        var assemblies = lost.SelectMany(package => package.LoadedAssemblies).Where(loaded => loaded.Assembly != null)
            .Select(loaded => loaded.Assembly).Distinct().ToArray();
        ValidateAuthoredTypeUsage(referencer, new([], [], assemblies), deletingAssets);
        // A standalone package has no evaluated source project/target framework.
        // Never certify C# usage through an invented project or empty Roslyn result.
        if (lost.Length != 0 && referencer.Package.Assets.Any(item => deletingAssets?.Contains(item.Id) != true && item.Asset is IProjectAsset))
            throw new InvalidOperationException("C# dependency usage must be verified before removing this dependency.");
    }
    internal void RemoveStandaloneDependencies(StandalonePackage referencer, DependencyRange[] removing,
        IReadOnlySet<AssetId>? deletingAssets = null)
    {
        ValidateStandaloneRemoval(referencer, removing, deletingAssets);
        var owner = history.Registry.GetOrCreate(referencer.Package.FullPath.ToOSPath(), null,
            () => new PackageMetadataDocument(workspace, referencer.Package, history)).Document;
        owner.RemoveSessionDependencies(removing, () => ValidateStandaloneRemoval(referencer, removing, deletingAssets));
    }
    internal void AddStandaloneDependency(StandalonePackage referencer, Package? loaded, PackageName? suggested)
    {
        Check(referencer);
        if (referencer.Package.State < PackageState.AssetsReady) throw new InvalidOperationException("This package can't be edited.");
        var name = loaded == null ? suggested?.Id : System.IO.Path.GetFileNameWithoutExtension(loaded.FullPath.ToOSPath());
        if (name == null) throw new InvalidOperationException("The dependency target changed.");
        var dependency = new DependencyRange(name, loaded?.Meta.Version is { } version ? new PackageVersionRange(version, true)
            : suggested != null ? new PackageVersionRange(suggested.Version, true) : null!,
            loaded?.Container is SolutionProject ? DependencyType.Project : DependencyType.Package)
        { MSBuildProject = (loaded?.Container as SolutionProject)?.FullPath.ToOSPath()! };
        var targetPath = loaded?.FullPath.ToOSPath();
        var targetName = loaded?.Meta.Name; var targetVersion = loaded?.Meta.Version?.ToString();
        void Validate(bool forward)
        {
            Check(referencer);
            if (forward)
            {
                if (referencer.DirectDependencies.Any(value => string.Equals(value.Name, dependency.Name, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("This package is already a dependency.");
                if (loaded != null && (!GetCandidates(referencer).Contains(loaded) || !workspace.Session.Packages.Contains(loaded)
                    || loaded.FullPath.ToOSPath() != targetPath || loaded.Meta.Name != targetName || loaded.Meta.Version?.ToString() != targetVersion))
                    throw new InvalidOperationException("This package cannot be added as a dependency.");
            }
            else ValidateStandaloneRemoval(referencer, [dependency]);
        }
        var owner = history.Registry.GetOrCreate(referencer.Package.FullPath.ToOSPath(), null,
            () => new PackageMetadataDocument(workspace, referencer.Package, history)).Document;
        owner.AddSessionDependency(dependency, Validate);
    }

}
