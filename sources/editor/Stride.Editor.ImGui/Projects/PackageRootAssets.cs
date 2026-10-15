using System.Reflection;
using Stride.Core.Assets;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

/// <summary>Build-root selection on the real owning or in-scope package.</summary>
internal sealed class PackageRootAssets(ProjectWorkspace workspace, EditorSessionHistory history)
{
    public static bool IsForced(AssetItem asset) => asset.Asset.GetType().GetCustomAttribute<AssetDescriptionAttribute>()?.AlwaysMarkAsRoot == true;
    private static bool IsInScope(Package target, AssetItem asset) => ReferenceEquals(target, asset.Package)
        || target.Container.FlattenedDependencies.Any(dependency => ReferenceEquals(dependency.Package, asset.Package));
    public static Package[] GetTargets(ProjectWorkspace workspace, IReadOnlyList<AssetItem> assets) => assets.Count == 0 ? []
        : workspace.Session.LocalPackages.Where(package => !package.IsReadOnly && package.State >= PackageState.AssetsReady && assets.All(asset => IsInScope(package, asset))).ToArray();
    public static Package? GetDefaultTarget(ProjectWorkspace workspace, IReadOnlyList<AssetItem> assets, IReadOnlyList<Package> targets)
    {
        if (targets.Count == 0) return null;
        var owners = assets.Select(asset => asset.Package).Distinct().ToArray();
        if (owners.Length == 1 && targets.Contains(owners[0])) return owners[0];
        if (workspace.Session.CurrentProject is { } current)
        {
            var libraries = targets.Where(package => !ReferenceEquals(package, current.Package)
                && package.Container is not SolutionProject { Type: ProjectType.Executable }
                && current.FlattenedDependencies.Any(dependency => ReferenceEquals(dependency.Package, package))).ToArray();
            var topmost = libraries.Where(package => !libraries.Any(other => !ReferenceEquals(package, other)
                && other.Container.FlattenedDependencies.Any(dependency => ReferenceEquals(dependency.Package, package)))).ToArray();
            if (topmost.Length == 1) return topmost[0];
            if (targets.Contains(current.Package)) return current.Package;
        }
        return targets[0];
    }
    public bool Toggle(IReadOnlyList<AssetId> ids, Package target)
    {
        if (target.IsReadOnly || target.State < PackageState.AssetsReady || !ReferenceEquals(target.Session, workspace.Session)
            || !workspace.Session.LocalPackages.Contains(target)) throw new InvalidOperationException("This package can't be edited.");
        var assets = ids.Distinct().Select(id => workspace.Session.FindAsset(id)
            ?? throw new InvalidOperationException("The asset no longer exists.")).ToArray();
        if (assets.Any(asset => !IsInScope(target, asset))) throw new InvalidOperationException("The asset is outside this package's scope.");
        var toggleable = assets.Where(asset => !IsForced(asset)).ToArray();
        if (toggleable.Length == 0) return false;
        var include = !toggleable.All(asset => target.RootAssets.ContainsKey(asset.Id));
        var selected = toggleable.Select(asset => asset.Id).ToHashSet();
        var before = target.RootAssets.ToArray();
        var after = before.Where(reference => !selected.Contains(reference.Id)).Concat(include
            ? toggleable.Select(asset => new AssetReference(asset.Id, asset.Location)) : []).ToArray();
        var metadata = PackageMetadataRevision.Prepare(history, workspace, [target]).Single();
        var operation = new RootOperation(target, before, after, metadata);
        var wasDirty = target.IsDirty;
        try
        {
            RootOperation.Apply(target, after); metadata.Apply(true);
            using var transaction = history.CreateTransaction("Change root assets");
            history.PushOperation(operation, metadata.State);
        }
        catch { RootOperation.Apply(target, before); metadata.Apply(false); target.IsDirty = wasDirty; throw; }
        return true;
    }
    private sealed class RootOperation(Package target, AssetReference[] before, AssetReference[] after, PackageMetadataRevision metadata) : Operation
    {
        internal static void Apply(Package package, AssetReference[] references)
        {
            var previous = package.RootAssets.ToArray();
            try { package.RootAssets.Clear(); foreach (var reference in references) package.RootAssets.Add(reference); package.IsDirty = true; }
            catch { package.RootAssets.Clear(); foreach (var reference in previous) package.RootAssets.Add(reference); throw; }
        }
        protected override void Rollback() => Replay(false);
        protected override void Rollforward() => Replay(true);
        private void Replay(bool forward)
        {
            var expected = forward ? before : after;
            if (target.RootAssets.Count != expected.Length || expected.Any(reference =>
                !target.RootAssets.Any(current => current.Id == reference.Id && current.Location == reference.Location)))
                throw new InvalidOperationException("The package root assets changed.");
            Apply(target, forward ? after : before); metadata.Apply(forward);
        }
    }
}
