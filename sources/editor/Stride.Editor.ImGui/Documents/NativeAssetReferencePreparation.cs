using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets.Tracking;
using Stride.Core.IO;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Core.Serialization;
using Stride.Core.Serialization.Contents;
using Stride.Core.Storage;

namespace Stride.Editor.ImGui;

internal static class NativeAssetReferencePreparation
{
    private sealed record PreparedLocationNode(IGraphNode Node, NodeIndex Index, ItemId Item, object? Before, object? After);
    internal static PreparedAssetReferenceEdit? Prepare(AssetPropertyGraph graph, IReadOnlyDictionary<AssetId, string> locations,
        string? previousPath, string? nextPath, long preparedRevision, Func<long> currentRevision,
        Func<IGraphNode, NodeIndex, bool> canEdit, Func<Action, string, Guid> applyMutation)
    {
        void ValidateLocationNode(IGraphNode node, NodeIndex index)
        {
            if (!canEdit(node, index)) throw new InvalidOperationException("This reference is read-only or sealed.");
        }
        var changes = new List<PreparedLocationNode>();
        var oldFolder = previousPath == null ? null : System.IO.Path.GetDirectoryName(previousPath)!;
        var newFolder = nextPath == null ? null : System.IO.Path.GetDirectoryName(nextPath)!;
        var rebasing = oldFolder != null && newFolder != null && !string.Equals(oldFolder, newFolder, StringComparison.OrdinalIgnoreCase);
        foreach (var link in AssetReferenceAnalysis.Visit(graph.AuthoredItem.Asset))
        {
            string? location = null;
            if (link.Reference is IReference reference && locations.TryGetValue(reference.Id, out var target)
                && !string.Equals(reference.Location, target, StringComparison.Ordinal)) location = target;
            else if (rebasing && link.Reference is UPath source && !source.IsAbsolute && !string.IsNullOrEmpty(source.FullPath)
                && link.Path.GetCustomAttribute<UPathAttribute>()?.RelativeTo != UPathRelativeTo.None)
            {
                var absolute = System.IO.Path.GetFullPath(source.ToOSPath(), oldFolder!);
                var rebased = System.IO.Path.GetRelativePath(newFolder!, absolute).Replace('\\', '/');
                if (!string.Equals(source.FullPath, rebased, StringComparison.Ordinal)) location = rebased;
            }
            if (location == null) continue;
            var path = GraphNodePath.From(graph.RootNode, link.Path, out var index);
            var node = path.GetNode();
            ValidateLocationNode(node, index);
            var before = node is IMemberNode member ? member.Retrieve() : ((IObjectNode)node).Retrieve(index);
            object? after = before switch
            {
                UrlReferenceBase url => UrlReferenceBase.New(url.GetType(), url.Id, location),
                AssetReference asset => new AssetReference(asset.Id, location),
                UFile => new UFile(location),
                UDirectory => new UDirectory(location),
                _ when AttachedReferenceManager.GetAttachedReference(before) is { } attached
                    => AttachedReferenceManager.CreateProxyObject(before!.GetType(), attached.Id, location),
                _ => throw new InvalidOperationException("This reference type has no native relocation adapter."),
            };
            if (before != null && !before.GetType().IsInstanceOfType(after)) throw new InvalidOperationException("The relocated reference has an incompatible type.");
            var itemId = node is IAssetObjectNode collection && collection.TryIndexToId(index, out var captured) ? captured : ItemId.Empty;
            changes.Add(new(node, index, itemId, before, after));
        }
        if (rebasing && graph.RootNode.Members.FirstOrDefault(node => node.Name == SourceHashesHelper.MemberName) is { } hashesNode
            && hashesNode.Retrieve() is Dictionary<UFile, ObjectId> hashes && hashes.Count != 0)
        {
            ValidateLocationNode(hashesNode, NodeIndex.Empty);
            var rebased = hashes.ToDictionary(entry => entry.Key.IsAbsolute ? entry.Key
                : new UFile(System.IO.Path.GetRelativePath(newFolder!, System.IO.Path.GetFullPath(entry.Key.ToOSPath(), oldFolder!)).Replace('\\', '/')), entry => entry.Value);
            changes.Add(new(hashesNode, NodeIndex.Empty, ItemId.Empty, hashes, rebased));
        }
        if (changes.Count == 0) return null;
        return new(preparedRevision, true, currentRevision, () =>
        {
            if (currentRevision() != preparedRevision && changes.Any(change => !SameValue(ReadValue(change), change.After)))
                throw new InvalidOperationException("The asset changed after relocation preparation.");
            foreach (var change in changes) ValidateLocationNode(change.Node, ResolveLocationIndex(change));
            return applyMutation(() =>
            {
                foreach (var change in changes)
                {
                    var index = ResolveLocationIndex(change);
                    var current = change.Node is IMemberNode member ? member.Retrieve() : ((IObjectNode)change.Node).Retrieve(index);
                    if (SameValue(current, change.After)) continue;
                    if (!SameValue(current, change.Before)) throw new InvalidOperationException("The reference changed after relocation preparation.");
                    if (change.Node is IMemberNode target) target.Update(change.After); else ((IObjectNode)change.Node).Update(change.After, index);
                }
            }, "Update asset references");
        });
    }
    private static NodeIndex ResolveLocationIndex(PreparedLocationNode change)
    {
        if (change.Item == ItemId.Empty) return change.Index;
        var collection = (IAssetObjectNode)change.Node;
        if (!collection.HasId(change.Item)) throw new InvalidOperationException("The referenced item no longer exists.");
        return collection.IdToIndex(change.Item);
    }
    private static object? ReadValue(PreparedLocationNode change)
        => change.Node is IMemberNode member ? member.Retrieve() : ((IObjectNode)change.Node).Retrieve(ResolveLocationIndex(change));
    private static bool SameValue(object? first, object? second)
    {
        if (ReferenceEquals(first, second) || Equals(first, second)) return true;
        if (first == null || second == null || first.GetType() != second.GetType()) return false;
        if (first is IReference left && second is IReference right)
            return left.Id == right.Id && string.Equals(left.Location, right.Location, StringComparison.Ordinal);
        if (first is Dictionary<UFile, ObjectId> leftHashes && second is Dictionary<UFile, ObjectId> rightHashes)
            return leftHashes.Count == rightHashes.Count && leftHashes.All(entry => rightHashes.TryGetValue(entry.Key, out var value) && value == entry.Value);
        var firstAttached = AttachedReferenceManager.GetAttachedReference(first);
        var secondAttached = AttachedReferenceManager.GetAttachedReference(second);
        return firstAttached != null && secondAttached != null && firstAttached.Id == secondAttached.Id
            && string.Equals(firstAttached.Url, secondAttached.Url, StringComparison.Ordinal);
    }
}
