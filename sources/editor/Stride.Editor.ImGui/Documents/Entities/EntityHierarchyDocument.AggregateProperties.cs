using Stride.Core.Assets;
using Stride.Core.Reflection;
using Stride.Core.Assets.Quantum;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal bool CanEditAggregateProperty(ComponentField field)
        => SessionHistory != null && ReferenceEquals(SessionHistory.Registry.Find(Asset.Id)?.Document, this)
        && field.Component.Entity is { } entity && IsSelected(entity.Id) && IsKnownComponentField(field) && field.SupportsEditing
        && (field.IsContentReference || field.IsSceneReference ? CanEditHierarchyField(field) && AssetMetadataDocument.CanAssignAuthoringNode(field.Node)
            && (!IsModelReference(field) || CanEditAggregateModelReference(field))
            : field.Node is IAssetMemberNode { BaseNode: not null } ? CanOverrideInheritedComponent(field)
                : CanEditHierarchyField(field) && AssetMetadataDocument.CanAssignAuthoringNode(field.Node) && field.Presentation?.IsReadOnly(field) != true);
    private bool CanEditAggregateModelReference(ComponentField field)
    {
        if (!IsModelReference(field)) return false;
        var materials = ModelMaterialsNode((ModelComponent)field.Component);
        return CanEditHierarchyNode(materials) && (materials.Indices ?? []).All(index => CanEditHierarchyItem(materials, index));
    }
    internal object? NormalizeAggregateProperty(ComponentField field, object? value)
    {
        if (!CanEditAggregateProperty(field)) throw new InvalidOperationException("This selected property cannot be edited.");
        var normalized = field.NormalizeValue(value);
        if (normalized == null ? field.Type.IsValueType && !field.IsNullable || !field.Type.IsValueType && !field.AllowsNullObject : !field.Type.IsInstanceOfType(normalized))
            throw new ArgumentException("The property value is incompatible.");
        if (field.IsContentReference) ContentAssetReference.Validate(field.Type, normalized, field.AllowsNullObject);
        if (field.IsSceneReference && normalized != null)
        {
            var target = normalized is Entity targetEntity ? targetEntity : (normalized as EntityComponent)?.Entity;
            if (!SceneObjectReference.Accepts(field.Type, normalized) || target == null
                || !Asset.Hierarchy.Parts.TryGetValue(target.Id, out var part) || !ReferenceEquals(part.Entity, target)
                || normalized is EntityComponent component && !target.Components.Contains(component))
                throw new ArgumentException("The reference must belong to every selected property's document.");
        }
        return normalized;
    }
    internal void EditAggregatePropertyFields(IReadOnlyList<ComponentField> fields, IReadOnlyList<object?> values)
    {
        if (fields.Count != values.Count || fields.Count == 0 || fields.Any(field => !CanEditAggregateProperty(field)))
            throw new InvalidOperationException("The aggregate property owners changed.");
        var unique = new List<int>();
        for (var index = 0; index < fields.Count; index++)
        {
            var same = unique.FirstOrDefault(other => ReferenceEquals(fields[other].Node, fields[index].Node), -1);
            if (same < 0) unique.Add(index);
            else if (fields[index].IsContentReference ? !ContentAssetReference.AreEqual(values[same], values[index]) : !Equals(values[same], values[index]))
                throw new InvalidOperationException("Aliases of one actual property cannot receive different values.");
        }
        if (unique.Count != fields.Count)
        {
            values = unique.Select(index => values[index]).ToArray();
            fields = unique.Select(index => fields[index]).ToArray();
        }
        if (fields.All(field => field.IsContentReference || field.IsSceneReference))
        {
            ApplyInheritedGraphMutation(() =>
            {
                for (var index = 0; index < fields.Count; index++)
                {
                    var field = fields[index];
                    if (!CanEditAggregateProperty(field)) throw new InvalidOperationException("The selected reference changed.");
                    var value = values[index];
                    if (field.IsContentReference ? ContentAssetReference.AreEqual(field.Node.Retrieve(), value) : Equals(field.Node.Retrieve(), value)) continue;
                    if (IsModelReference(field)) Stride.Assets.Presentation.NodePresenters.Rules.ModelMaterialNodeRules.Clear(ModelMaterialsNode((ModelComponent)field.Component));
                    field.Node.Update(value);
                }
            }, "Edit " + fields[0].Label);
            return;
        }
        if (fields.All(IsModelReference)) { UpdateModelReferences(fields, values.ToArray()); return; }
        if (fields.All(field => EditorHierarchyGraphs.IsScalar(field.Type) || field.IsContentReference || field.IsSceneReference))
        {
            ApplyInheritedGraphMutation(() =>
            {
                for (var index = 0; index < fields.Count; index++)
                {
                    var field = fields[index];
                    if (!CanEditAggregateProperty(field)) throw new InvalidOperationException("The selected property changed.");
                    var node = (IAssetMemberNode)field.Node;
                    var sealedFlag = node.GetContentOverride() & OverrideType.Sealed;
                    node.Update(values[index]);
                    node.RestoreContentOverride(node.GetContentOverride() | sealedFlag);
                }
            }, "Edit " + fields[0].Label);
            return;
        }
        throw new InvalidOperationException("This aggregate property requires a specialized editor.");
    }
}
