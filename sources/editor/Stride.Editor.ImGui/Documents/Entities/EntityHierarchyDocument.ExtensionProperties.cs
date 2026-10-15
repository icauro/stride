using Stride.Core.Assets.Quantum;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal EditorPresenterTarget? AcquireExtensionCommandTarget(Stride.Core.Quantum.IMemberNode node)
    {
        foreach (var presentation in propertyPresenters)
            if (presentation.AcquireCommandTarget(node) is { } target) return target;
        return null;
    }
    internal bool CanEditExtensionProperty(ComponentField field)
        => CanSave && SessionHistory != null && ReferenceEquals(SessionHistory.Registry.Find(Asset.Id)?.Document, this)
        && IsKnownComponentField(field) && field.Node.MemberDescriptor.HasSet && field.Presentation?.IsReadOnly(field) != true
        && field.Component.Entity is { } owner && IsSelected(owner.Id) && Asset.Hierarchy.Parts.TryGetValue(owner.Id, out var design)
        && ReferenceEquals(design.Entity, owner) && owner.Components.Contains(field.Component)
        && CanEditHierarchyField(field) && AssetMetadataDocument.CanAssignAuthoringNode(field.Node);
    internal void UpdateExtensionProperty(ComponentField field, object? value)
    {
        if (!CanEditExtensionProperty(field)) throw new InvalidOperationException("This extension property is read-only or sealed.");
        value = field.NormalizeValue(value);
        if (value == null ? field.Type.IsValueType && !field.IsNullable || !field.Type.IsValueType && !field.AllowsNullObject
            : !field.Type.IsInstanceOfType(value)) throw new ArgumentException("The extension value is incompatible with this property.");
        if (field.IsContentReference) ContentAssetReference.Validate(field.Type, value, field.AllowsNullObject);
        if (field.IsSceneReference) throw new InvalidOperationException("Scene references require their native reference picker.");
        ApplyInheritedGraphMutation(() => field.Node.Update(value), "Edit " + field.Label);
    }
}
