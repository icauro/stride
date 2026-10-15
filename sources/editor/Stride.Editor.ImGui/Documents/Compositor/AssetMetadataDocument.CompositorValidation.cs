using Stride.Core;
using Stride.Core.Quantum;
using Stride.Rendering;
using Stride.Rendering.Compositing;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    /// <summary>Validate actual renderer references before recursive engine collection.</summary>
    public IReadOnlyList<string> ValidateCompositorGraph(bool requireEditor = false)
    {
        if (CompositorAsset == null) return [];
        var errors = new List<string>();
        var renderers = new Dictionary<Guid, ISharedRenderer>();
        foreach (var renderer in CompositorAsset.SharedRenderers)
        {
            if (renderer == null) { errors.Add("The shared renderer collection contains a null renderer."); continue; }
            if (renderer.Id == Guid.Empty) errors.Add("A shared renderer has an empty identifier.");
            if (!renderers.TryAdd(renderer.Id, renderer)) errors.Add("Duplicate shared renderer identifier: " + renderer.Id);
        }
        foreach (var collection in new[] { CompositorAsset.RenderStages.Cast<IIdentifiable>(), CompositorAsset.Cameras.Cast<IIdentifiable>() })
        {
            var ids = new HashSet<Guid>();
            foreach (var item in collection)
                if (item == null || item.Id == Guid.Empty || !ids.Add(item.Id)) errors.Add("A compositor stage or camera slot has a missing or duplicate identifier.");
        }
        if (errors.Count != 0) return errors;
        var blocks = CompositorBlocks;
        foreach (var block in blocks)
            foreach (var slot in block.Slots)
                if (slot.Renderer is { } renderer)
                {
                    if (!renderers.ContainsKey(renderer.Id)) errors.Add(block.Title + "." + slot.Name + ": the shared renderer is not part of this compositor.");
                    if (!slot.Accessor.AcceptValue(renderer)) errors.Add(block.Title + "." + slot.Name + ": the renderer type does not match the slot.");
                }
        var edges = new Dictionary<IGraphicsRendererBase, HashSet<IGraphicsRendererBase>>(ReferenceEqualityComparer.Instance);
        var stages = new HashSet<RenderStage>(CompositorAsset.RenderStages, ReferenceEqualityComparer.Instance);
        var cameras = new HashSet<SceneCameraSlot>(CompositorAsset.Cameras, ReferenceEqualityComparer.Instance);
        var walked = new HashSet<(IObjectNode Node, IGraphicsRendererBase? Owner)>();
        void Edge(IGraphicsRendererBase? owner, object? value)
        {
            if (value is RenderStage stage && !stages.Contains(stage)) errors.Add("A render stage reference is not part of this compositor: " + stage.Id);
            if (value is SceneCameraSlot camera && !cameras.Contains(camera)) errors.Add("A camera slot reference is not part of this compositor: " + camera.Id);
            if (value is not IGraphicsRendererBase renderer) return;
            if (!edges.ContainsKey(renderer)) edges.Add(renderer, new(ReferenceEqualityComparer.Instance));
            if (owner != null) edges[owner].Add(renderer);
            if (renderer is ISharedRenderer shared && (!renderers.TryGetValue(shared.Id, out var owned) || !ReferenceEquals(owned, shared)))
                errors.Add("A shared renderer reference is not part of this compositor: " + shared.Id);
        }
        void Walk(IObjectNode node, IGraphicsRendererBase? owner)
        {
            if (node.Retrieve() is IGraphicsRendererBase renderer) { owner = renderer; Edge(null, renderer); }
            if (!walked.Add((node, owner))) return;
            foreach (var member in node.Members)
            {
                var value = member.Retrieve(); Edge(owner, value);
                if (member.Target is { } target && !AuthoringGraph.Definition.IsMemberTargetObjectReference(member, value)) Walk(target, owner);
            }
            foreach (var index in node.Indices ?? [])
            {
                var value = node.Retrieve(index); Edge(owner, value);
                if (node.ItemReferences != null && node.IndexedTarget(index) is { } target
                    && !AuthoringGraph.Definition.IsTargetItemObjectReference(node, index, value)) Walk(target, owner);
            }
        }
        Walk(AuthoringGraph.RootNode, null);
        var adjacency = new Dictionary<IGraphicsRendererBase, IGraphicsRendererBase[]>(ReferenceEqualityComparer.Instance);
        foreach (var pair in edges) adjacency.Add(pair.Key, pair.Value.ToArray());
        var colors = new Dictionary<IGraphicsRendererBase, byte>(ReferenceEqualityComparer.Instance);
        foreach (var root in edges.Keys)
        {
            if (colors.GetValueOrDefault(root) == 2) continue;
            var stack = new Stack<(IGraphicsRendererBase Renderer, int Child)>(); stack.Push((root, 0)); colors[root] = 1;
            var cycle = false;
            while (stack.TryPop(out var current))
            {
                var children = adjacency[current.Renderer];
                if (current.Child == children.Length) { colors[current.Renderer] = 2; continue; }
                stack.Push((current.Renderer, current.Child + 1));
                var next = children[current.Child]; var color = colors.GetValueOrDefault(next);
                if (color == 1) { cycle = true; break; }
                if (color == 2) continue;
                colors[next] = 1; stack.Push((next, 0));
            }
            if (cycle) { errors.Add("The compositor renderer graph contains a cycle."); break; }
        }
        if (requireEditor && CompositorAsset.Editor == null) errors.Add("The authored compositor has no editor renderer.");
        return errors;
    }
}
