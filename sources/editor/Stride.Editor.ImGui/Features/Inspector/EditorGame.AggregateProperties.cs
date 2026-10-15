using Stride.Core;
using Stride.Core.Assets.Quantum;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly Dictionary<ComponentField, EntityHierarchyDocument> aggregatePropertyOwners = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(EntityHierarchyDocument Owner, long Selection, long Structure), int> aggregateOwnerVersions = [];
    private IReadOnlyList<CommonComponentProperty> aggregateProperties = [];
    private readonly Dictionary<(string Property, int Axis), string> aggregatePropertyDrafts = [];
    private IReadOnlyList<CommonComponentProperty> ReadAggregateProperties()
    {
        var owners = SelectedHierarchyDocuments;
        if (owners.Count == aggregateOwnerVersions.Count && owners.All(owner => aggregateOwnerVersions.ContainsKey((owner, owner.SelectionVersion, owner.StructureVersion)))) return aggregateProperties;
        aggregateOwnerVersions.Clear(); aggregatePropertyOwners.Clear(); aggregatePropertyDrafts.Clear();
        commonInheritedColorDrafts.Clear(); commonColorHexDrafts.Clear();
        var maps = new List<Dictionary<string, (ComponentField Field, string Label)>>();
        static IEnumerable<ComponentField> Leaves(IEnumerable<ComponentField> fields)
        {
            foreach (var field in fields)
                if (field.IsIndexedCollection || field.IsDictionary) yield return field;
                else if (field.SupportsEditing || field.Children.Count == 0 && !field.IsObjectProperty) yield return field;
                else
                {
                    if (field.SupportsObjectLifecycle) yield return field;
                    foreach (var child in Leaves(field.Children)) yield return child;
                }
        }
        foreach (var owner in owners)
        {
            aggregateOwnerVersions[(owner, owner.SelectionVersion, owner.StructureVersion)] = owner.SelectedCount;
            foreach (var id in owner.SelectedIds)
            {
                var entity = owner.Asset.Hierarchy.Parts[id].Entity;
                var map = new Dictionary<string, (ComponentField, string)>(); var occurrences = new Dictionary<Type, int>();
                foreach (var component in entity.Components)
                {
                    var type = component.GetType(); var occurrence = occurrences.GetValueOrDefault(type); occurrences[type] = occurrence + 1;
                    var prefix = component is TransformComponent ? "Transform" : type.AssemblyQualifiedName + "@" + occurrence;
                    var label = component is TransformComponent ? "Transform" : DisplayAttribute.GetDisplayName(type) ?? type.Name;
                    foreach (var field in Leaves(owner.GetComponentFields(component)))
                    {
                        if (component is TransformComponent && field.Node.Name is not (nameof(TransformComponent.Position) or nameof(TransformComponent.Rotation) or nameof(TransformComponent.Scale))) continue;
                        map[prefix + "/" + field.Name] = (field, label); aggregatePropertyOwners[field] = owner;
                    }
                }
                maps.Add(map);
            }
        }
        if (maps.Count < 2) return aggregateProperties = [];
        var result = new List<CommonComponentProperty>();
        foreach (var entry in maps[0])
        {
            var fields = new List<ComponentField> { entry.Value.Field };
            foreach (var map in maps.Skip(1))
            {
                if (!map.TryGetValue(entry.Key, out var other) || other.Field.DeclaredType != entry.Value.Field.DeclaredType
                    || !Equals(other.Field.Node.MemberDescriptor.MemberInfo, entry.Value.Field.Node.MemberDescriptor.MemberInfo)) break;
                fields.Add(other.Field);
            }
            if (fields.Count == maps.Count) result.Add(new(entry.Key, entry.Value.Label, fields));
        }
        return aggregateProperties = result;
    }
    private bool CanEditAggregateProperty(CommonComponentProperty property)
        => property.Fields.All(field => aggregatePropertyOwners.TryGetValue(field, out var owner)
            && ReferenceEquals(owner.SessionHistory, sessionHistory) && owner.CanEditAggregateProperty(field));
    private void ApplyAggregateProperty(CommonComponentProperty property, Func<ComponentField, object?> createValue)
    {
        if (sessionHistory == null || !CanEditAggregateProperty(property)) throw new InvalidOperationException("These property owners cannot be edited together.");
        var owners = property.Fields.Select(field => aggregatePropertyOwners[field]).Distinct().ToArray();
        foreach (var owner in owners) owner.EndEdits();
        var values = property.Fields.Select(field => aggregatePropertyOwners[field].NormalizeAggregateProperty(field, createValue(field))).ToArray();
        var ownedFlags = property.First.Type.IsEnum && property.First.Type.IsDefined(typeof(FlagsAttribute), false);
        var changed = Enumerable.Range(0, property.Fields.Count).Where(index => !ownedFlags
            || property.Fields[index].Node is not IAssetMemberNode { BaseNode: null }
            || !Equals(property.Fields[index].Node.Retrieve(), values[index])).ToArray();
        // Explicit inherited equal-value edits still establish overrides. Only
        // already-equal owned flag values can be omitted from this UI command.
        if (changed.Length == 0) return;
        var leases = new List<IDisposable>();
        try
        {
            foreach (var owner in owners) leases.Add(sessionHistory.Registry.Find(owner.Asset.Id)!.AcquireView());
            using var prepared = sessionHistory.CaptureOperationsAtomic(() =>
            {
                foreach (var owner in owners)
                {
                    var indices = changed.Where(index => ReferenceEquals(aggregatePropertyOwners[property.Fields[index]], owner)).ToArray();
                    if (indices.Length == 0) continue;
                    owner.EditAggregatePropertyFields(indices.Select(index => property.Fields[index]).ToArray(), indices.Select(index => values[index]).ToArray());
                }
            });
            prepared.Publish(sessionHistory, "Edit " + property.First.Label);
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }
}
