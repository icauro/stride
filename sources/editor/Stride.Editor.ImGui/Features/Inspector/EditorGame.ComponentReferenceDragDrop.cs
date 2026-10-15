using Hexa.NET.ImGui;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private const string ComponentReferenceDragType = "STRIDE_COMPONENT_REFERENCE";
    private struct ComponentReferenceDragPayload
    {
        internal Guid Workspace, Document, Entity, Component;
        internal long Structure;
    }

    private EntityComponent? ResolveComponentReferenceDrag(ComponentReferenceDragPayload payload)
    {
        var owner = document;
        if (workspace == null || owner == null || sessionHistory?.Registry.Find(owner.Asset.Id) is not { IsPublished: true } state
            || !ReferenceEquals(state.Document, owner) || payload.Workspace != assetDragIdentity || payload.Document != owner.DragIdentity
            || payload.Structure != owner.StructureVersion || payload.Component == Guid.Empty
            || !owner.Asset.Hierarchy.Parts.TryGetValue(payload.Entity, out var part)) return null;
        EntityComponent? result = null;
        foreach (var component in part.Entity.Components)
        {
            if (component.Id != payload.Component) continue;
            if (result != null || !ReferenceEquals(component.Entity, part.Entity)) return null;
            result = component;
        }
        return result;
    }

    private unsafe void DrawComponentReferenceDragSource(EntityComponent component)
    {
        var owner = document;
        if (workspace == null || owner == null || component.Entity == null) return;
        var data = new ComponentReferenceDragPayload
        {
            Workspace = assetDragIdentity, Document = owner.DragIdentity, Entity = component.Entity.Id,
            Component = component.Id, Structure = owner.StructureVersion,
        };
        if (!ReferenceEquals(ResolveComponentReferenceDrag(data), component) || !Gui.BeginDragDropSource()) return;
        Gui.SetDragDropPayload(ComponentReferenceDragType, &data, (nuint)sizeof(ComponentReferenceDragPayload));
        Gui.TextUnformatted(SceneReferenceLabel(component)); Gui.EndDragDropSource();
    }

    private unsafe bool AcceptComponentReferenceDrop(Type targetType, Action<object> assign)
    {
        var payload = Gui.AcceptDragDropPayload(ComponentReferenceDragType, ImGuiDragDropFlags.AcceptBeforeDelivery);
        if (payload.IsNull) return false;
        if (payload.DataSize != sizeof(ComponentReferenceDragPayload)) return true;
        var data = *(ComponentReferenceDragPayload*)payload.Data;
        var component = ResolveComponentReferenceDrag(data);
        if (component != null && SceneObjectReference.SupportsComponent(targetType) && SceneObjectReference.Accepts(targetType, component))
        {
            if (payload.IsDelivery()) assign(component);
        }
        return true;
    }
}
