using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Reflection;
using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    private long commonSelectionVersion = -1, commonStructureVersion = -1;
    private IReadOnlyList<CommonComponentProperty> commonComponentProperties = [];
    private CommonComponentProperty? activeCommonProperty;
    private object?[] commonEditStart = [];
    private bool[] commonEditOverrides = [];
    private long commonPropertySynchronizationVersion;
    private EditorDeferredAssetMutation? commonEditMutation;
    private bool committingCommonEdit;
    private readonly List<EditorDeferredAssetMutation> commonMutationCleanup = [];
    internal bool HasActiveCommonPropertyChange => commonEditMutation?.HasChanges == true || activeCommonProperty != null
        && activeCommonProperty.Fields.Where((member, index) => !Equals(member.Node.Retrieve(), commonEditStart[index])).Any();

    internal IReadOnlyList<CommonComponentProperty> GetCommonComponentProperties()
    {
        if (commonSelectionVersion == SelectionVersion && commonStructureVersion == StructureVersion) return commonComponentProperties;
        commonSelectionVersion = SelectionVersion; commonStructureVersion = StructureVersion;
        var entities = SelectedIds.Select(id => Asset.Hierarchy.Parts[id].Entity).ToArray();
        if (entities.Length < 2) return commonComponentProperties = [];
        static IEnumerable<ComponentField> CommonFields(IEnumerable<ComponentField> fields)
        {
            foreach (var field in fields)
            {
                if (field.IsIndexedCollection || field.IsDictionary) continue;
                if (field.SupportsEditing || field.Children.Count == 0 && !field.IsObjectProperty) yield return field;
                else foreach (var child in CommonFields(field.Children)) yield return child;
            }
        }
        var maps = entities.Select(entity =>
        {
            var occurrences = new Dictionary<Type, int>();
            var fields = new Dictionary<string, (ComponentField Field, string Label)>();
            foreach (var component in entity.Components.Where(component => component is not TransformComponent))
            {
                var type = component.GetType(); var occurrence = occurrences.GetValueOrDefault(type); occurrences[type] = occurrence + 1;
                var key = type.AssemblyQualifiedName + "@" + occurrence;
                var label = DisplayAttribute.GetDisplayName(type) ?? type.Name;
                foreach (var field in CommonFields(GetComponentFields(component)))
                {
                    // Owned objects are expanded; collection mutations and lifecycle commands have distinct semantics.
                    fields.Add(key + "/" + field.Name, (field, label));
                }
            }
            return fields;
        }).ToArray();
        var result = new List<CommonComponentProperty>();
        foreach (var entry in maps[0])
        {
            var fields = new List<ComponentField> { entry.Value.Field };
            foreach (var map in maps.Skip(1))
            {
                if (!map.TryGetValue(entry.Key, out var candidate) || candidate.Field.DeclaredType != entry.Value.Field.DeclaredType
                    || !Equals(candidate.Field.Node.MemberDescriptor.MemberInfo, entry.Value.Field.Node.MemberDescriptor.MemberInfo)) break;
                fields.Add(candidate.Field);
            }
            if (fields.Count == entities.Length) result.Add(new(entry.Key, entry.Value.Label, fields));
        }
        return commonComponentProperties = result;
    }

    internal bool CanEditCommonProperty(CommonComponentProperty property) => SelectedCount > 1 && property.SupportsEditing
        && property.Fields.All(CanEditAggregateProperty);

    internal void UpdateCommonProperty(CommonComponentProperty property, object? value)
        => UpdateCommonPropertyValues(property, _ => value);

    internal void UpdateCommonPropertyValues(CommonComponentProperty property, Func<ComponentField, object?> createValue)
    {
        if (!CanEditCommonProperty(property)) throw new InvalidOperationException("These properties cannot be edited together.");
        var values = property.Fields.Select(field =>
        {
            var value = createValue(field);
            if (field.IsContentReference) ContentAssetReference.Validate(field.Type, value, field.AllowsNullObject);
            if (value == null && (field.Type.IsValueType && !field.IsNullable || !field.Type.IsValueType && !field.AllowsNullObject)) throw new ArgumentException("This property cannot contain null.");
            var normalized = field.NormalizeValue(value);
            if (normalized != null && !field.Type.IsInstanceOfType(normalized)) throw new ArgumentException("The property value is not compatible.");
            if (field.IsSceneReference && normalized != null)
            {
                var entity = normalized is Entity target ? target : (normalized as EntityComponent)?.Entity;
                if (!SceneObjectReference.Accepts(field.Type, normalized) || entity == null || !Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var part) || !ReferenceEquals(part.Entity, entity)
                    || normalized is EntityComponent component && !entity.Components.Contains(component)) throw new ArgumentException("The reference must belong to this scene.");
            }
            return normalized;
        }).ToArray();
        if (property.Fields.All(IsModelReference)) { UpdateModelReferences(property.Fields, values); return; }
        if (SessionHistory != null)
        {
            if (activeCommonProperty != property)
            {
                EndEdits();
                commonEditMutation = BeginExtensionCommandMutation();
                activeCommonProperty = property;
                commonEditStart = property.Fields.Select(field => field.Node.Retrieve()).ToArray();
            }
            if (!CanEditCommonProperty(property)) throw new InvalidOperationException("The selected property owners changed.");
            try
            {
                for (var index = 0; index < property.Fields.Count; index++)
                {
                    var field = property.Fields[index];
                    if (!CanEditAggregateProperty(field)) throw new InvalidOperationException("The selected property owners changed.");
                    var node = (IAssetMemberNode)field.Node;
                    var sealedFlag = node.GetContentOverride() & OverrideType.Sealed;
                    node.Update(values[index]); node.RestoreContentOverride(node.GetContentOverride() | sealedFlag);
                }
                commonPropertySynchronizationVersion++;
            }
            catch
            {
                var mutation = commonEditMutation!;
                commonEditMutation = null; activeCommonProperty = null; commonEditStart = []; commonEditOverrides = [];
                try { mutation.Dispose(); } catch { commonMutationCleanup.Add(mutation); throw; }
                throw;
            }
            return;
        }
        if (activeCommonProperty != property)
        {
            EndEdits(); activeCommonProperty = property;
            commonEditStart = property.Fields.Select(field => field.Node.Retrieve()).ToArray();
            commonEditOverrides = property.Fields.Select(field => ((IAssetMemberNode)field.Node).IsContentOverridden()).ToArray();
        }
        ApplyCommonValues(property, values, null);
    }

    private void ApplyCommonValues(CommonComponentProperty property, object?[] values, bool[]? overrides)
    {
        var previous = property.Fields.Select(field => field.Node.Retrieve()).ToArray();
        var previousOverrides = property.Fields.Select(field => ((IAssetMemberNode)field.Node).IsContentOverridden()).ToArray();
        var last = -1;
        try
        {
            for (var i = 0; i < property.Fields.Count; i++)
            {
                last = i; var node = (IAssetMemberNode)property.Fields[i].Node;
                node.Update(values[i]); if (overrides != null) node.OverrideContent(overrides[i]);
            }
        }
        catch
        {
            for (var i = last; i >= 0; i--)
            {
                var node = (IAssetMemberNode)property.Fields[i].Node;
                node.Update(previous[i]); node.OverrideContent(previousOverrides[i]);
            }
            throw;
        }
        finally
        {
            if (property.Fields.Where((field, index) => !Equals(previous[index], field.Node.Retrieve())).Any()) commonPropertySynchronizationVersion++;
        }
    }

    internal void EndCommonPropertyEdit()
    {
        ReleaseCommonMutationCleanup();
        if (committingCommonEdit) return;
        if (activeCommonProperty == null) return;
        if (commonEditMutation != null)
        {
            var editingProperty = activeCommonProperty; var mutation = commonEditMutation;
            committingCommonEdit = true;
            try
            {
                mutation.Commit("Edit " + editingProperty.First.Label);
                // Clear only after a successful commit; disposal retry cannot replay it.
                commonEditMutation = null; activeCommonProperty = null; commonEditStart = []; commonEditOverrides = [];
                try { mutation.Dispose(); } catch { commonMutationCleanup.Add(mutation); throw; }
                LastAction = "Edit " + editingProperty.First.Label;
            }
            finally { committingCommonEdit = false; }
            return;
        }
        var property = activeCommonProperty; var before = commonEditStart; var beforeOverrides = commonEditOverrides;
        var after = property.Fields.Select(field => field.Node.Retrieve()).ToArray();
        var afterOverrides = property.Fields.Select(field => ((IAssetMemberNode)field.Node).IsContentOverridden()).ToArray();
        activeCommonProperty = null; commonEditStart = []; commonEditOverrides = [];
        if (before.SequenceEqual(after)) return;
        var beforeRevision = revision; var afterRevision = ++nextRevision;
        void Set(object?[] values, bool[] overrides, long currentRevision)
        { ApplyCommonValues(property, values, overrides); revision = currentRevision; }
        revision = afterRevision;
        using (CreateDocumentTransaction()) RecordOperation(new HierarchyCreationOperation(
            () => Set(before, beforeOverrides, beforeRevision), () => Set(after, afterOverrides, afterRevision)));
        LastAction = "Edit " + property.First.Label;
    }
    internal void ReleaseCommonMutationCleanup()
    { foreach (var mutation in commonMutationCleanup.ToArray()) { mutation.Dispose(); commonMutationCleanup.Remove(mutation); } }
}
