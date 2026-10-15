using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    private readonly List<ComponentNodePresentation> propertyPresenters = [];
    private IReadOnlyList<ComponentField> AttachPropertyPresentation(IObjectNode root, IReadOnlyList<ComponentField> fields, Type? rootDeclaredType = null)
    {
        var extensions = SessionHistory?.Extensions?.AcquirePresenters(Asset, rootDeclaredType ?? root.Retrieve()?.GetType());
        var presentation = new ComponentNodePresentation(root, hierarchyGraph!.Container.NodeContainer.NodeBuilder, fields, rootDeclaredType, extensions,
            SessionHistory?.Extensions is { } services ? services.HasPrimitiveEditor : null,
            (collection, index) => !hierarchyGraph!.Definition.IsTargetItemObjectReference(collection, index, collection.Retrieve(index)),
            member => !hierarchyGraph!.Definition.IsMemberTargetObjectReference(member, member.Retrieve()), CanEditHierarchyNode);
        propertyPresenters.Add(presentation);
        ComponentField Attach(ComponentField field) => field with { Presentation = presentation, Children = field.Children.Select(Attach).ToArray() };
        return fields.Select(Attach).ToArray();
    }

    internal ComponentNodePresentation GetComponentPresentation(Stride.Engine.EntityComponent component)
    {
        GetComponentFields(component);
        return propertyPresenters.Single(presentation => ReferenceEquals(presentation.AuthoredValue, component));
    }

    internal void DisposePropertyPresenters()
    {
        EndComponentVirtualEdit(); ReleaseComponentVirtualTargets();
        foreach (var presentation in propertyPresenters) presentation.Dispose();
        propertyPresenters.Clear();
    }
}
