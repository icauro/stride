using Stride.Assets.Rendering;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Extensions;
using Stride.Core.Mathematics;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Rendering;
using Stride.Rendering.Compositing;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private long compositorModelRevision = -1;
    private IReadOnlyList<CompositorBlock> compositorBlocks = [];
    private IReadOnlyList<CompositorListItem> compositorItems = [];
    public GraphicsCompositorAsset? CompositorAsset => AuthoredAsset as GraphicsCompositorAsset;
    public bool CanEditCompositor => CompositorAsset != null && CanEditArchetypeAsset;
    public IReadOnlyList<CompositorBlock> CompositorBlocks { get { RefreshCompositorModel(); return compositorBlocks; } }
    public IReadOnlyList<CompositorListItem> CompositorItems { get { RefreshCompositorModel(); return compositorItems; } }
    private void RefreshCompositorModel()
    {
        if (CompositorAsset == null || compositorModelRevision == EditRevision && compositorBlocks.Count > 0) return;
        var graph = AuthoringGraph; var root = graph.RootNode;
        var entries = new CompositorReferenceVisitor(graph.Definition, Guid.Empty);
        foreach (var name in new[] { nameof(GraphicsCompositorAsset.Game), nameof(GraphicsCompositorAsset.SingleView), nameof(GraphicsCompositorAsset.Editor) }) entries.Visit(root[name]);
        var blocks = new List<CompositorBlock> { new(Guid.Empty, CompositorAsset, root, "Entry points", entries.Slots) };
        var renderers = (IAssetObjectNode)root[nameof(GraphicsCompositorAsset.SharedRenderers)].Target;
        foreach (var index in renderers.Indices ?? [])
        {
            var renderer = (ISharedRenderer)renderers.Retrieve(index); var node = renderers.IndexedTarget(index)!;
            var slots = new CompositorReferenceVisitor(graph.Definition, renderer.Id); slots.Visit(node);
            blocks.Add(new(renderer.Id, renderer, node, DisplayAttribute.GetDisplayName(renderer.GetType()), slots.Slots));
        }
        var items = new List<CompositorListItem>();
        foreach (var name in new[] { nameof(GraphicsCompositorAsset.RenderStages), nameof(GraphicsCompositorAsset.RenderFeatures), nameof(GraphicsCompositorAsset.Cameras) })
        {
            var collection = (IAssetObjectNode)root[name].Target;
            foreach (var index in collection.Indices ?? []) items.Add(new(name, collection.IndexToId(index), collection.Retrieve(index), collection.IndexedTarget(index)!));
        }
        compositorBlocks = blocks; compositorItems = items; compositorModelRevision = EditRevision;
    }
    public static IReadOnlyList<Type> CompositorRendererTypes() => typeof(ISharedRenderer).GetInheritedInstantiableTypes()
        .Where(type => TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<NonInstantiableAttribute>(type) == null
            && TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<ObsoleteAttribute>(type) == null && type.GetConstructor(Type.EmptyTypes) != null)
        .OrderBy(DisplayAttribute.GetDisplayName, StringComparer.Ordinal).ToArray();
    public static IReadOnlyList<Type> CompositorFeatureTypes() => typeof(RootRenderFeature).GetInheritedInstantiableTypes()
        .Where(type => ObjectFactoryRegistry.CanCreateInstance(type) && TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<NonInstantiableAttribute>(type) == null)
        .OrderBy(DisplayAttribute.GetDisplayName, StringComparer.Ordinal).ToArray();
    public bool CanAddCompositorItem(string name) => CanEditCompositor && AuthoringGraph.RootNode[name].Target is IAssetObjectNode node && CanEditAssetSettingNode(node);
    public bool CanRemoveCompositorItem(CompositorListItem item) => CanEditCompositor && AuthoringGraph.RootNode[item.Collection].Target is IAssetObjectNode node
        && node.HasId(item.ItemId) && ReferenceEquals(node.Retrieve(node.IdToIndex(item.ItemId)), item.Value)
        && CanEditAssetSettingItem(node, node.IdToIndex(item.ItemId));
    public bool CanMoveCompositorBlock(Guid id)
    {
        if (!CanEditCompositor || !CompositorBlocks.Any(block => block.Id == id)
            || AuthoringGraph.RootNode[nameof(GraphicsCompositorAsset.BlockPositions)].Target is not IAssetObjectNode node || !CanEditAssetSettingNode(node)) return false;
        return !CompositorAsset!.BlockPositions.ContainsKey(id) || CanEditAssetSettingItem(node, new NodeIndex(id));
    }
    private IAssetObjectNode CompositorCollection(string name)
    {
        if (!CanEditCompositor) throw new InvalidOperationException("This graphics compositor cannot be edited.");
        var collection = (IAssetObjectNode)AuthoringGraph.RootNode[name].Target;
        if (!CanEditAssetSettingNode(collection)) throw new InvalidOperationException("This compositor collection is read-only.");
        return collection;
    }
    public void AddCompositorItem(string collectionName, Type? type = null, Vector2? position = null)
    {
        var collection = CompositorCollection(collectionName);
        object value = collectionName switch
        {
            nameof(GraphicsCompositorAsset.RenderStages) => new RenderStage(),
            nameof(GraphicsCompositorAsset.Cameras) => new SceneCameraSlot(),
            nameof(GraphicsCompositorAsset.RenderFeatures) when type != null && CompositorFeatureTypes().Contains(type) => ObjectFactoryRegistry.NewInstance(type),
            nameof(GraphicsCompositorAsset.SharedRenderers) when type != null && CompositorRendererTypes().Contains(type) => Activator.CreateInstance(type)!,
            _ => throw new ArgumentException("The compositor type cannot be created.", nameof(type)),
        };
        AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
        var positions = position != null ? CompositorCollection(nameof(GraphicsCompositorAsset.BlockPositions)) : null;
        ApplyInheritedAssetMutation(() =>
        {
            collection.Add(value);
            if (positions != null && value is ISharedRenderer renderer && position is { } point) positions.Add(point, new NodeIndex(renderer.Id));
        }, collectionName == nameof(GraphicsCompositorAsset.SharedRenderers) ? "Create renderer" : "Create new " + (value is RenderStage ? "render stage" : value is SceneCameraSlot ? "camera slot" : "render feature"));
    }
    public bool CanEditCompositorSlot(CompositorSlot slot)
    {
        RefreshCompositorModel();
        if (!CanEditCompositor || !compositorBlocks.Any(block => block.Slots.Contains(slot))) return false;
        var accessor = slot.Accessor;
        return accessor.IsItem ? accessor.Node is IAssetObjectNode list && CanEditAssetSettingItem(list, accessor.Index)
            : accessor.Node is IMemberNode member && CanEditAssetSettingNode(member) && CanAssignAuthoringNode(member);
    }
    public void ConnectCompositorSlot(CompositorSlot slot, Guid? rendererId)
    {
        if (!CanEditCompositorSlot(slot)) throw new InvalidOperationException("This renderer link is read-only.");
        var value = rendererId is { } id ? CompositorAsset!.SharedRenderers.FirstOrDefault(renderer => renderer.Id == id)
            ?? throw new InvalidOperationException("The renderer no longer exists.") : null;
        var accessor = slot.Accessor;
        if (!accessor.AcceptValue(value)) throw new InvalidOperationException("The renderer does not match the slot type.");
        if (ReferenceEquals(accessor.RetrieveValue(), value)) return;
        ApplyInheritedAssetMutation(() => accessor.UpdateValue(value), value == null ? "Clear link" : "Connect " + slot.Name + " to " + value.Name);
    }
    public void RemoveCompositorItems(IEnumerable<CompositorListItem> selected)
    {
        var items = selected.Distinct().ToArray(); if (items.Length == 0) return;
        foreach (var item in items)
        {
            var collection = CompositorCollection(item.Collection);
            if (!CanRemoveCompositorItem(item)) throw new InvalidOperationException("This compositor item is read-only or no longer current.");
        }
        ApplyInheritedAssetMutation(() =>
        {
            foreach (var item in items)
            {
                var collection = CompositorCollection(item.Collection);
                // WPF intentionally leaves SceneCameraSlotId references in scene assets.
                if (item.Value is RenderStage stage) AuthoringGraph.ClearReferencesToObjects(new[] { stage.Id });
                collection.Remove(item.Value, collection.IdToIndex(item.ItemId));
            }
        }, "Delete compositor item(s)");
    }
    public void DeleteCompositorSelection(IEnumerable<Guid> selectedBlocks, IEnumerable<CompositorSlot> selectedLinks)
    {
        var ids = selectedBlocks.Where(id => id != Guid.Empty).Distinct().ToArray();
        var links = selectedLinks.Distinct().ToArray(); var collection = CompositorCollection(nameof(GraphicsCompositorAsset.SharedRenderers));
        foreach (var id in ids)
        {
            var index = CompositorAsset!.SharedRenderers.ToList().FindIndex(renderer => renderer.Id == id);
            if (index < 0 || !CanEditAssetSettingItem(collection, new NodeIndex(index))) throw new InvalidOperationException("This renderer cannot be removed.");
        }
        foreach (var slot in links)
            if (!CanEditCompositorSlot(slot)) throw new InvalidOperationException("The selected renderer link is read-only or no longer current.");
        foreach (var slot in CompositorBlocks.SelectMany(block => block.Slots))
            if ((links.Contains(slot) || slot.Renderer is { } renderer && ids.Contains(renderer.Id)) && !CanEditCompositorSlot(slot))
                throw new InvalidOperationException("A renderer link is read-only.");
        if (ids.Length == 0 && links.Length == 0) return;
        ApplyInheritedAssetMutation(() =>
        {
            foreach (var slot in links) slot.Accessor.UpdateValue(null);
            foreach (var id in ids)
            {
                var index = CompositorAsset!.SharedRenderers.ToList().FindIndex(renderer => renderer.Id == id); var renderer = CompositorAsset.SharedRenderers[index];
                AuthoringGraph.ClearReferencesToObjects(new[] { id }); collection.Remove(renderer, new NodeIndex(index));
            }
        }, "Delete selection");
    }
    public void SetCompositorBlockPositions(IReadOnlyDictionary<Guid, Vector2> positions)
    {
        var node = CompositorCollection(nameof(GraphicsCompositorAsset.BlockPositions));
        var updates = positions.Where(pair => float.IsFinite(pair.Value.X) && float.IsFinite(pair.Value.Y)
            && (!CompositorAsset!.BlockPositions.TryGetValue(pair.Key, out var previous) || previous != pair.Value)).ToArray();
        if (updates.Length == 0) return;
        if (updates.Any(pair => !CanMoveCompositorBlock(pair.Key))) throw new InvalidOperationException("A compositor block position is read-only.");
        ApplyInheritedAssetMutation(() =>
        {
            foreach (var pair in updates)
                if (CompositorAsset!.BlockPositions.ContainsKey(pair.Key)) node.Update(pair.Value, new NodeIndex(pair.Key));
                else node.Add(pair.Value, new NodeIndex(pair.Key));
        }, "Move renderer(s)");
    }
    public void MoveCompositorListItem(CompositorListItem item, int destination)
    {
        var collection = CompositorCollection(item.Collection);
        if (!CanRemoveCompositorItem(item) || collection.Descriptor is not CollectionDescriptor { HasInsert: true }
            || destination < 0 || destination >= collection.Indices!.Count()) throw new InvalidOperationException("This compositor item cannot be reordered.");
        var index = collection.IdToIndex(item.ItemId); if (index.Int == destination) return;
        ApplyInheritedAssetMutation(() =>
        {
            var overridden = collection.IsItemOverridden(index);
            collection.RemoveAndDiscard(item.Value, index, item.ItemId);
            var next = new NodeIndex(destination); collection.Restore(item.Value, next, item.ItemId); collection.OverrideItem(overridden, next);
        }, "Move compositor item");
    }
}
