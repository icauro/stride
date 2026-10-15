using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets.Quantum.Visitors;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Rendering;
using Stride.Rendering.Compositing;

namespace Stride.Editor.ImGui;

internal sealed record CompositorSlot(Guid Owner, string Name, GraphNodePath Path)
{
    public NodeAccessor Accessor => Path.GetAccessor();
    public ISharedRenderer? Renderer => Accessor.RetrieveValue() as ISharedRenderer;
}
internal sealed record CompositorBlock(Guid Id, object Value, IObjectNode Node, string Title, IReadOnlyList<CompositorSlot> Slots);
internal sealed record CompositorListItem(string Collection, ItemId ItemId, object Value, IObjectNode Node)
{
    public string SlotLabel { get; } = Value is RenderStage stage ? " (Slot " + stage.EffectSlotName + ")" : "";
    public string Name => Value switch
    {
        RenderStage stage => stage.Name,
        SceneCameraSlot camera => camera.Name,
        _ => DisplayAttribute.GetDisplayName(Value.GetType()),
    };
}

/// <summary>WPF's actual owned-renderer reference discovery, stopped at object-reference boundaries.</summary>
internal sealed class CompositorReferenceVisitor(AssetPropertyGraphDefinition definition, Guid owner) : AssetGraphVisitorBase(definition)
{
    public readonly List<CompositorSlot> Slots = [];
    protected override void VisitMemberTarget(IMemberNode node)
    {
        Process(node, NodeIndex.Empty, node.Type);
        base.VisitMemberTarget(node);
    }
    protected override void VisitItemTargets(IObjectNode node)
    {
        if (node.ItemReferences != null)
            foreach (var item in node.ItemReferences) Process(node, item.Index, node.ItemReferences.ElementType);
        base.VisitItemTargets(node);
    }
    private void Process(IGraphNode node, NodeIndex index, Type type)
    {
        if (!typeof(IGraphicsRendererBase).IsAssignableFrom(type) || node is IMemberNode { MemberDescriptor.HasSet: false }) return;
        var value = node.Retrieve(index);
        if (value != null && value is not ISharedRenderer) return; // Inlined renderers remain property nodes.
        var path = CurrentPath.Clone(); if (!index.IsEmpty) path.PushIndex(index);
        var name = string.Concat(path.Path.Where(element => element.Type != GraphNodePath.ElementType.Target).Select(element => element.ToString())).TrimStart('.');
        if (path.RootNode is IMemberNode member) name = member.Name + name;
        Slots.Add(new(owner, name, path));
    }
}
