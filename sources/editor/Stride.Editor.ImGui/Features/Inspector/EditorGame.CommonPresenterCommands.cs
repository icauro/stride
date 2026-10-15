namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawCommonMemberPresenterCommands(CommonComponentProperty property, bool aggregate)
    {
        var fields = property.Fields.ToArray(); var commonOwner = document!;
        var owners = fields.Select(field => aggregate ? aggregatePropertyOwners[field] : commonOwner).ToArray();
        bool Current() => sessionHistory is { } history && (aggregate || ReferenceEquals(document, commonOwner))
            && fields.Select((field, index) => (!aggregate || aggregatePropertyOwners.TryGetValue(field, out var current) && ReferenceEquals(current, owners[index]))
                && owners[index].CanCopyProperty(field) && field.Component.Entity is { } entity && owners[index].IsSelected(entity.Id)
                && history.Registry.Find(owners[index].Asset.Id) is { IsPublished: true, IsDisposed: false } state
                && ReferenceEquals(state.Document, owners[index])).All(current => current);
        DrawHierarchyPresenterCommands(owners, Current, () => AcquireHierarchyFactoryTargets(fields.Length,
            index => owners[index].AcquireExtensionCommandTarget(fields[index].Node)));
    }
}
