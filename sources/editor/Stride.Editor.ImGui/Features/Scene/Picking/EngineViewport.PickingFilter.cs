using Stride.Engine;

namespace Stride.Editor.ImGui;

internal sealed partial class EngineViewport
{
    private Func<Entity, bool>? pickingTypeFilter;
    private long pickingTypeFilterVersion = -1;
    internal void SetPickingTypeFilter(Func<Entity, bool> filter, long version)
    {
        if (ReferenceEquals(pickingTypeFilter, filter) && pickingTypeFilterVersion == version) return;
        pickingTypeFilter = filter; pickingTypeFilterVersion = version;
        Picker?.Cancel("Picking filters changed");
    }
    private bool IsEntityTypePickable(Entity entity)
    {
        var owner = RuntimeOwner(entity);
        var authored = owner != null && owner.Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var part) ? part.Entity : entity;
        return pickingTypeFilter?.Invoke(authored) != false;
    }
}
