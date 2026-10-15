using Stride.Assets.Quantum;
using Stride.UI.Assets;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.UI;
using Stride.UI.Controls;
using Stride.UI.Panels;

namespace Stride.Editor.ImGui;

internal sealed partial class UIDocument
{
    internal Guid[] OrderedHierarchyRoots(IEnumerable<Guid> ids)
    {
        var requested = ids.Distinct().ToArray();
        if (requested.Any(id => Find(id) == null)) throw new InvalidOperationException("The selected UI element no longer exists.");
        var roots = CommonRoots(requested).ToHashSet();
        return Roots.SelectMany(Enumerate).Where(element => roots.Contains(element.Id)).Select(element => element.Id).ToArray();
    }
    internal bool CanMoveHierarchyFrom(UIDocument source, IReadOnlyList<Guid> ids, Guid? parentId, int index)
    {
        if (!CanSave || !source.CanSave || ids.Count == 0 || SessionHistory == null || !ReferenceEquals(SessionHistory, source.SessionHistory)) return false;
        if (SessionHistory.Registry.Find(Asset.Id) is not { IsPublished: true } destinationOwner || !ReferenceEquals(destinationOwner.Document, this)
            || SessionHistory.Registry.Find(source.Asset.Id) is not { IsPublished: true } sourceOwner || !ReferenceEquals(sourceOwner.Document, source)) return false;
        if (ids.Distinct().Count() != ids.Count || ids.Any(id => source.Find(id) == null)) return false;
        var parent = parentId is { } value ? Find(value) : null;
        if (parentId != null && parent == null || parent != null && parent is not Panel and not ContentControl) return false;
        if (index < 0 || index > (parent?.VisualChildren.Count ?? Roots.Count)) return false;
        var roots = source.CommonRoots(ids);
        if (roots.Count != ids.Count) return false;
        if (ReferenceEquals(source, this) && parent != null
            && roots.Any(id => Find(id) == parent || Ancestors(parent).Any(ancestor => ancestor.Id == id))) return false;
        // Content controls and pages retain their native single-child/root capacity.
        var retained = parent == null ? Roots.Count : parent.VisualChildren.Count;
        if (ReferenceEquals(source, this)) retained -= roots.Count(id => ReferenceEquals(source.Require(id).VisualParent, parent));
        return parent is not ContentControl && (parent != null || Asset is not UIPageAsset) || retained + roots.Count <= 1;
    }
    internal bool HasHierarchyLibraryDependency(AssetId destination, IEnumerable<Guid> ids, Func<AssetId, UIDocument?> resolve)
    {
        var pending = new Stack<AssetId>(ids.SelectMany(id => Enumerate(Require(id)))
            .Select(element => Asset.Hierarchy.Parts[element.Id].Base).OfType<BasePart>().Select(link => link.BasePartAsset.Id));
        var visited = new HashSet<AssetId>();
        while (pending.TryPop(out var id))
        {
            if (id == destination) return true;
            if (!visited.Add(id)) continue;
            var library = resolve(id);
            if (library == null) return true;
            foreach (var link in library.Asset.Hierarchy.Parts.Values.Select(part => part.Base).OfType<BasePart>()) pending.Push(link.BasePartAsset.Id);
        }
        return false;
    }
    internal IReadOnlyDictionary<Guid, Guid> MoveHierarchyFrom(UIDocument source, IReadOnlyList<Guid> ids, Guid? parentId, int index,
        Func<AssetId, UIDocument?> resolve)
    {
        CommitEdits(); source.CommitEdits();
        var ordered = source.OrderedHierarchyRoots(ids);
        if (!CanMoveHierarchyFrom(source, ordered, parentId, index)) throw new InvalidOperationException("The UI destination cannot accept these elements.");
        if (!ReferenceEquals(source, this) && source.HasHierarchyLibraryDependency(Asset.Id, ordered, resolve))
            throw new InvalidOperationException("Element depends on this asset");
        var parent = parentId is { } value ? Require(value) : null;
        var originals = ordered.Select(source.Require).ToArray();
        if (ReferenceEquals(source, this))
        {
            var insertion = index - originals.Count(element => ReferenceEquals(element.VisualParent, parent) && Asset.IndexOf(element) < index);
            var siblings = parent?.VisualChildren.ToArray() ?? Roots.ToArray();
            var remaining = siblings.Where(element => !ordered.Contains(element.Id)).Select(element => element.Id).ToList();
            remaining.InsertRange(insertion, ordered);
            if (originals.All(element => ReferenceEquals(element.VisualParent, parent)) && remaining.SequenceEqual(siblings.Select(element => element.Id)))
                return ordered.ToDictionary(id => id, id => id);
            var parts = new AssetPartCollection<UIElementDesign, UIElement>();
            foreach (var root in originals) foreach (var element in Enumerate(root)) parts.Add(Asset.Hierarchy.Parts[element.Id]);
            var designs = ordered.Select(id => Asset.Hierarchy.Parts[id]).ToArray();
            Mutate(() =>
            {
                foreach (var design in designs) UIHierarchyGraph.RemovePartFromAsset(design);
                foreach (var design in designs) UIHierarchyGraph.AddPartToAsset(parts, design, parent, insertion++);
            }, "Move UI elements");
            return parts.Keys.ToDictionary(id => id, id => id);
        }
        var hierarchy = UIAssetPropertyGraph.CloneSubHierarchies(source.UIHierarchyGraph.Container.NodeContainer,
            UIHierarchyGraph.Container.NodeContainer, source.Asset, ordered,
            SubHierarchyCloneFlags.GenerateNewIdsForIdentifiableObjects, out Dictionary<Guid, Guid> remapping);
        PrepareHierarchyLibraryLinks([hierarchy], resolve);
        using var capture = SessionHistory!.CaptureOperationsAtomic(() =>
        {
            source.Delete(ordered);
            Mutate(() =>
            {
                foreach (var root in hierarchy.RootParts)
                    UIHierarchyGraph.AddPartToAsset(hierarchy.Parts, hierarchy.Parts[root.Id], parent, index++);
                UIHierarchyGraph.RefreshBase(); UIHierarchyGraph.ReconcileWithBase();
            }, "Move UI elements");
        });
        capture.Publish(SessionHistory, "Move UI elements");
        return remapping;
    }
    private void PrepareHierarchyLibraryLinks(IEnumerable<AssetCompositeHierarchyData<UIElementDesign, UIElement>> hierarchies, Func<AssetId, UIDocument?> resolve)
    {
        var identities = Asset.Hierarchy.Parts.Keys.ToHashSet();
        foreach (var hierarchy in hierarchies)
            foreach (var design in hierarchy.Parts.Values)
            {
                if (!identities.Add(design.UIElement.Id)) throw new InvalidOperationException("The copied UI hierarchy has conflicting identifiers.");
                if (design.Base is not { } link) continue;
                var library = resolve(link.BasePartAsset.Id) ?? throw new InvalidOperationException("A copied source UI library is unavailable.");
                if (library.Asset.Id == Asset.Id || library.Find(link.BasePartId) == null) throw new InvalidOperationException("The copied UI library reference is invalid.");
                var pending = new Stack<UIDocument>(); pending.Push(library); var visited = new HashSet<AssetId>();
                while (pending.TryPop(out var dependency))
                {
                    if (dependency.Asset.Id == Asset.Id) throw new InvalidOperationException("The copied UI library depends on its destination.");
                    if (!visited.Add(dependency.Asset.Id)) continue;
                    foreach (var inherited in dependency.Asset.Hierarchy.Parts.Values.Select(part => part.Base).OfType<BasePart>())
                        pending.Push(resolve(inherited.BasePartAsset.Id) ?? throw new InvalidOperationException("A copied UI library dependency is unavailable."));
                    SessionHistory!.UiLibraryGraphs.RegisterPreparedBase(this, dependency);
                }
                SessionHistory!.UiLibraryGraphs.RegisterPreparedBase(this, library);
            }
    }
}
