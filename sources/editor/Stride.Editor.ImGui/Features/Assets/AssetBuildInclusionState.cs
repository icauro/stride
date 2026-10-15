using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;

namespace Stride.Editor.ImGui;

internal sealed record AssetBuildInclusion(string Icon, string Description, string? ReplacesUrl, string? ReplacedByUrl);

/// <summary>WPF inclusion rules over the actual authored dependency snapshot and native roots.</summary>
internal sealed class AssetBuildInclusionState
{
    private ProjectWorkspace? workspace;
    private long dependencyVersion = -1;
    private int rootSignature;
    private readonly Dictionary<AssetId, AssetBuildInclusion> statuses = [];
    internal AssetBuildInclusion? Get(AssetId id) => statuses.GetValueOrDefault(id);
    internal void Clear() { workspace = null; dependencyVersion = -1; statuses.Clear(); }
    internal void Update(ProjectWorkspace current, AssetReferencesState references)
    {
        var project = current.Session.CurrentProject;
        var signature = new HashCode(); signature.Add(project);
        foreach (var package in current.Session.Packages)
        {
            signature.Add(package); signature.Add(package.State); signature.Add(package.IsReadOnly);
            foreach (var root in package.RootAssets) { signature.Add(root.Id); signature.Add(root.Location); }
        }
        if (project != null) foreach (var dependency in project.FlattenedDependencies) signature.Add(dependency.Package);
        var nextSignature = signature.ToHashCode();
        if (ReferenceEquals(workspace, current) && dependencyVersion == references.DependencyVersion && rootSignature == nextSignature) return;
        workspace = current; dependencyVersion = references.DependencyVersion; rootSignature = nextSignature; statuses.Clear();
        var snapshot = references.DependencySnapshot;
        var replacements = new Dictionary<AssetId, AssetItem>();
        foreach (var dependency in snapshot.Values)
            if (!dependency.Item.IsDeleted && dependency.Item.Asset.Replaces is { } replaces && snapshot.ContainsKey(replaces.Id))
                replacements.TryAdd(replaces.Id, dependency.Item);
        var roots = new HashSet<AssetId>();
        foreach (var dependency in snapshot.Values)
        {
            var item = dependency.Item;
            var inScope = project != null && (ReferenceEquals(project.Package, item.Package)
                || project.FlattenedDependencies.Any(candidate => ReferenceEquals(candidate.Package, item.Package)));
            if (!item.IsDeleted && inScope && (PackageRootAssets.IsForced(item) || project!.Package.RootAssets.ContainsKey(item.Id)
                || project.FlattenedDependencies.Any(candidate => candidate.Package?.RootAssets.ContainsKey(item.Id) == true))) roots.Add(item.Id);
        }
        var reachable = new HashSet<AssetId>(roots); var pending = new Queue<AssetId>(roots);
        while (pending.TryDequeue(out var id))
        {
            if (replacements.TryGetValue(id, out var replacement))
            { if (reachable.Add(replacement.Id)) pending.Enqueue(replacement.Id); continue; }
            if (!snapshot.TryGetValue(id, out var dependency)) continue;
            foreach (var link in dependency.LinksOut)
                if ((link.Type & ContentLinkType.Reference) != 0 && !link.Item.IsDeleted && reachable.Add(link.Item.Id)) pending.Enqueue(link.Item.Id);
        }
        foreach (var dependency in snapshot.Values)
        {
            var item = dependency.Item; var root = roots.Contains(item.Id); var included = reachable.Contains(item.Id) && !item.IsDeleted;
            var replacedBy = replacements.GetValueOrDefault(item.Id);
            var overridden = included && replacedBy != null;
            var icon = overridden ? "asset-build-overridden" : root ? "asset-build-root" : included ? "asset-build-dependency" : "asset-build-excluded";
            var description = overridden ? EditorLocalization.Text("Overridden in build (content not used)")
                : root ? PackageRootAssets.IsForced(item) ? EditorLocalization.Text("Included in build as root (always)", "Message")
                    : string.Format(EditorLocalization.Text("Included in build as root (in {0})", "Message"), string.Join(", ", current.Session.LocalPackages
                        .Where(package => package.RootAssets.ContainsKey(item.Id)).Select(package => Path.GetFileNameWithoutExtension(package.FullPath.ToOSPath()))))
                : included ? EditorLocalization.Text("Included in build as dependency") : EditorLocalization.Text("Excluded from build");
            statuses[item.Id] = new(icon, description, item.Asset.Replaces?.Location, replacedBy?.Location.ToString());
        }
    }
}
