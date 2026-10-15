using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.IO;
using Stride.Core.Extensions;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed record AssetRepairField(AssetRepairContext Context, NodeAccessor Accessor, Type Type,
    string Path, object? Before, ItemId Item, bool Source, AssetId? MissingId);

/// <summary>Actual canonical node access and journal ownership for explicit source/reference repair.</summary>
internal sealed class AssetRepairContext(EditorAuthoringState owner, Asset asset, AssetPropertyGraph graph,
    Func<IGraphNode, bool> canEdit, Func<IAssetObjectNode, NodeIndex, bool> canEditItem, Action<Action, string> mutate) : IDisposable
{
    private readonly IDisposable lease = owner.AcquireView();
    internal EditorAuthoringState Owner => owner;
    internal Asset Asset => asset;
    internal AssetPropertyGraph Graph => graph;
    internal bool IsCurrent => owner.IsPublished && !owner.IsDisposed && ReferenceEquals(owner.Registry.Find(asset.Id), owner);
    internal IReadOnlyList<AssetRepairField> MissingSources()
        => Fields(true, _ => true);
    internal IReadOnlyList<AssetRepairField> MissingReferences(Func<AssetId, bool> exists)
        => Fields(false, exists);
    private IReadOnlyList<AssetRepairField> Fields(bool source, Func<AssetId, bool> exists)
    {
        if (!IsCurrent) throw new InvalidOperationException("The asset repair owner changed.");
        var fields = new List<AssetRepairField>();
        var visitor = graph.CreateReconcilerVisitor();
        visitor.Visiting += (node, path) =>
        {
            // Proxy base fields do not describe the event stream written by the native serializer.
            if (UnknownAssetData.IsProxyNode(node)) return;
            for (var current = path; current != null; current = current.GetParent())
                if (UnknownAssetData.IsProxyNode(current.GetAccessor().Node)) return;
            if (node is IAssetMemberNode member)
            {
                if (member.IsContentInherited()) return;
                var sourceAttribute = member.MemberDescriptor.GetCustomAttributes<SourceFileMemberAttribute>(true).FirstOrDefault();
                if (source && member.Type == typeof(UFile) && sourceAttribute != null)
                {
                    var value = member.Retrieve() as UFile;
                    if (sourceAttribute.Optional && (value == null || string.IsNullOrWhiteSpace(value.ToString()))) return;
                    var absolute = value == null || string.IsNullOrWhiteSpace(value.ToString()) ? null : System.IO.Path.GetFullPath(value.ToOSPath(), System.IO.Path.GetDirectoryName(owner.Document.Path)!);
                    if (absolute == null || !File.Exists(absolute)) fields.Add(new(this, new(member, NodeIndex.Empty), member.Type, path.ToString(), value, ItemId.Empty, true, null));
                }
                else if (!source) Add(new(member, NodeIndex.Empty), member.Type, path.ToString(), ItemId.Empty);
            }
            else if (!source && node is IAssetObjectNode { IsEnumerable: true } collection)
                foreach (var index in collection.Indices!)
                    if (!collection.IsItemInherited(index)) Add(new(collection, index), collection.Descriptor.GetInnerCollectionType(), path + "[" + index + "]",
                        collection.TryIndexToId(index, out var id) ? id : ItemId.Empty);
        };
        visitor.Visit(graph.RootNode); return fields;
        void Add(NodeAccessor accessor, Type type, string path, ItemId item)
        {
            var value = accessor.RetrieveValue();
            if (!ContentAssetReference.Supports(type) || ContentAssetReference.Read(value) is not { } reference || exists(reference.Id)) return;
            fields.Add(new(this, accessor, type, path, value, item, false, reference.Id));
        }
    }
    private NodeAccessor Current(AssetRepairField field)
    {
        if (!ReferenceEquals(field.Context, this) || !IsCurrent) throw new InvalidOperationException("The asset repair owner changed.");
        var accessor = field.Accessor;
        var reachable = false;
        var visitor = graph.CreateReconcilerVisitor();
        visitor.Visiting += (node, _) => { if (ReferenceEquals(node, accessor.Node)) reachable = true; };
        visitor.Visit(graph.RootNode);
        if (!reachable) throw new InvalidOperationException("The authored repair node no longer belongs to this asset.");
        if (accessor.Node is IAssetObjectNode collection && field.Item != ItemId.Empty)
        {
            if (!collection.TryIdToIndex(field.Item, out var index)) throw new InvalidOperationException("The repaired reference item no longer exists.");
            accessor = new(collection, index);
        }
        var editable = accessor.Node is IMemberNode member
            ? canEdit(member) && AssetMetadataDocument.CanAssignAuthoringNode(member)
            : accessor.Node is IAssetObjectNode items && canEditItem(items, accessor.Index);
        if (!editable || !ContentAssetReference.AreEqual(accessor.RetrieveValue(), field.Before)) throw new InvalidOperationException("The authored repair field changed or is read-only.");
        return accessor;
    }
    internal void Validate(IReadOnlyList<(AssetRepairField Field, object? Value)> changes)
    {
        foreach (var change in changes)
            if (!Current(change.Field).AcceptValue(change.Value!)) throw new InvalidOperationException("A replacement has an incompatible actual property type.");
    }
    internal void Repair(IReadOnlyList<(AssetRepairField Field, object? Value)> changes)
    {
        var prepared = changes.Select(change => (Accessor: Current(change.Field), change.Value)).ToArray();
        if (prepared.Any(change => !change.Accessor.AcceptValue(change.Value!))) throw new InvalidOperationException("A replacement has an incompatible actual property type.");
        mutate(() => { foreach (var change in prepared) change.Accessor.UpdateValue(change.Value!); }, "Repair asset references");
    }
    public void Dispose() => lease.Dispose();
}
