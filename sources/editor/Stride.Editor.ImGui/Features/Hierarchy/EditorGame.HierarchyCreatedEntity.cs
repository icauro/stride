using Stride.Engine;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void CompleteCreatedHierarchyEntity(EntityHierarchyDocument owner, Entity entity, Guid? parent, string folder)
    {
        SelectCompositeHierarchyEntity(owner, entity, false);
        compositeHierarchyPrimary = (owner, entity.Id);
        RevealHierarchyContext(owner, entity.Id, parent, folder);
        BeginEntityRename(owner, entity);
    }
}
