using Stride.Core;
using Stride.Core.Quantum;
using Stride.Core.Transactions;
using Stride.Core.Reflection;
using Stride.Core.Serialization;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    private readonly Dictionary<EntityComponent, IReadOnlyList<ComponentField>> componentFields = [];
    private long extensionPresenterVersion = -1;
    private readonly Dictionary<EntityComponent, IReadOnlyList<ComponentField>> entityReferenceFields = [];
    internal IEnumerable<ComponentField> GetEntityReferenceFields()
    {
        EnsureHierarchyGraph();
        static bool ReferenceType(Type type) => typeof(Entity).IsAssignableFrom(type) || typeof(EntityComponent).IsAssignableFrom(type);
        foreach (var part in Asset.Hierarchy.Parts.Values)
        foreach (var component in part.Entity.Components)
        {
            if (!entityReferenceFields.TryGetValue(component, out var fields))
            {
                fields = Flatten(BuildFields(component, hierarchyGraph!.Container.NodeContainer.GetOrCreateNode(component), [], new HashSet<IObjectNode>(), 0))
                    .Where(field => !(component is TransformComponent && field.Name == nameof(TransformComponent.Children)) && (ReferenceType(field.Type) || field.Type.IsArray && ReferenceType(field.Type.GetElementType()!)
                        || field.Type.GetInterfaces().Append(field.Type).Any(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IList<>)
                            && ReferenceType(type.GetGenericArguments()[0]))))
                    .ToArray();
                entityReferenceFields.Add(component, fields);
            }
            foreach (var field in fields) yield return field;
        }
    }
    private readonly List<ComponentField> synchronizationFields = [];
    private ComponentField? activeComponentField;
    private object? componentEditStart;
    private EditorDeferredAssetMutation? componentEditMutation;
    private readonly List<EditorDeferredAssetMutation> componentMutationCleanup = [];
    private bool committingComponentEdit;
    private bool HasActiveComponentChange => componentEditMutation?.HasChanges == true
        || activeComponentField != null && !Equals(componentEditStart, activeComponentField.Node.Retrieve());
    internal IReadOnlyList<ComponentField> ObservedComponentFields => synchronizationFields;
    internal static IEnumerable<ComponentField> Flatten(IEnumerable<ComponentField> fields)
    {
        foreach (var field in fields)
        {
            yield return field;
            foreach (var child in Flatten(field.Children)) yield return child;
        }
    }

    public IReadOnlyList<ComponentField> GetComponentFields(EntityComponent component)
    {
        var extensionVersion = SessionHistory?.Extensions?.Version ?? 0;
        if (extensionPresenterVersion != extensionVersion)
        {
            EndEdits(); InvalidateComponentStructure(); extensionPresenterVersion = extensionVersion;
        }
        if (componentFields.TryGetValue(component, out var cached)) return cached;
        EnsureHierarchyGraph();
        var root = hierarchyGraph!.Container.NodeContainer.GetOrCreateNode(component);
        var fields = AttachPropertyPresentation(root, BuildFields(component, root, [], new HashSet<IObjectNode>(), 0));
        AddSynchronizationFields(fields);
        componentFields.Add(component, fields); return fields;
    }
    private void AddSynchronizationFields(IEnumerable<ComponentField> fields)
    {
        foreach (var field in fields)
        {
            if (field.Node.MemberDescriptor.HasSet && field.SupportsValueEditing) synchronizationFields.Add(field);
            else AddSynchronizationFields(field.Children);
        }
    }
    private static IReadOnlyList<ComponentField> BuildFields(EntityComponent component, IObjectNode owner,
        IReadOnlyList<IMemberDescriptor> prefix, HashSet<IObjectNode> ancestors, int depth)
    {
        if (depth >= 16 || !ancestors.Add(owner)) return [];
        var fields = owner.Members
            .Select(node => (Node: node, Display: TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>(node.MemberDescriptor.MemberInfo)))
            .Where(member => member.Node.Name != nameof(EntityComponent.Id) && member.Display?.Browsable != false)
            .Select(member =>
            {
                var node = member.Node;
                IMemberDescriptor[] path = [.. prefix, node.MemberDescriptor];
                var value = node.Retrieve();
                var label = member.Display?.Name ?? Stride.Core.Presentation.Core.Utils.SplitCamelCase(node.Name);
                var field = new ComponentField(component, node, label) { Path = path, Name = string.Join(".", path.Select(entry => entry.Name)) };
                // Expand owned serialized objects, never content references, collections or entity back-links.
                if (!field.IsNullable && (!field.SupportsEditing || field.IsColor) && value != null && value is not Entity && value is not EntityComponent
                    && value is not System.Collections.IEnumerable && AttachedReferenceManager.GetAttachedReference(value) == null
                    && value.GetType().IsDefined(typeof(DataContractAttribute), true)
                    && (!value.GetType().IsValueType || node.MemberDescriptor.HasSet) && node.Target is { } target)
                    field = field with { Children = BuildFields(component, target, path, ancestors, depth + 1) };
                return field;
            }).OrderBy(field => field, ComponentPropertyPresentation.FieldComparer).ToArray();
        ancestors.Remove(owner);
        return fields;
    }
    public void BeginComponentEdit(ComponentField field)
    {
        if (activeComponentField == field) return;
        EndEdits();
        if (!CanEditComponentValue(field))
            throw new InvalidOperationException("This component property is not editable in the current document.");
        if (SessionHistory != null) componentEditMutation = BeginExtensionCommandMutation();
        activeComponentField = field; componentEditStart = field.Node.Retrieve();
    }
    internal bool CanEditComponentValue(ComponentField field)
        => field.SupportsEditing && field.Component.Entity is { } owner && IsSelected(owner.Id)
            && CanEditHierarchyField(field) && AssetMetadataDocument.CanAssignAuthoringNode(field.Node)
            && field.Presentation?.IsReadOnly(field) != true;
    public void UpdateComponent(ComponentField field, object? value)
    {
        if (field.IsContentReference) ContentAssetReference.Validate(field.Type, value, field.AllowsNullObject);
        if (value == null && (field.Type.IsValueType && !field.IsNullable || !field.Type.IsValueType && !field.AllowsNullObject))
            throw new ArgumentException("This property cannot contain null.");
        if (field.IsSceneReference && value != null)
        {
            var entity = value is Entity referenced ? referenced : (value as EntityComponent)?.Entity;
            if (!SceneObjectReference.Accepts(field.Type, value) || entity == null
                || !Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var design) || !ReferenceEquals(design.Entity, entity)
                || value is EntityComponent component && !entity.Components.Contains(component))
                throw new ArgumentException("A scene reference must target a compatible entity or component in this document.");
        }
        value = field.NormalizeValue(value);
        if (IsModelReference(field)) { UpdateModelReferences([field], [value]); return; }
        BeginComponentEdit(field);
        if (!CanEditComponentValue(field)) throw new InvalidOperationException("The selected property owner changed.");
        try { field.Node.Update(value); }
        catch { CancelComponentEdit(field); throw; }
    }
    public void CancelComponentEdit(ComponentField field)
    {
        if (activeComponentField != field) return;
        if (componentEditMutation != null)
        {
            var mutation = componentEditMutation;
            componentEditMutation = null; activeComponentField = null; componentEditStart = null;
            try { mutation.Dispose(); } catch { componentMutationCleanup.Add(mutation); throw; }
            return;
        }
        field.Node.Update(componentEditStart);
        activeComponentField = null; componentEditStart = null;
    }
    public void EndComponentEdit(EntityComponent? component = null)
    {
        ReleaseComponentMutationCleanup();
        if (committingComponentEdit) return;
        if (activeComponentField == null || component != null && activeComponentField.Component != component) return;
        if (componentEditMutation != null)
        {
            var current = activeComponentField; var mutation = componentEditMutation;
            committingComponentEdit = true;
            try
            {
                mutation.Commit("Edit " + current.Label);
                componentEditMutation = null; activeComponentField = null; componentEditStart = null;
                try { mutation.Dispose(); } catch { componentMutationCleanup.Add(mutation); throw; }
                LastAction = "Edit " + current.Component.GetType().Name + ": " + current.Label;
            }
            finally { committingComponentEdit = false; }
            return;
        }
        var field = activeComponentField; var before = componentEditStart; var after = field.Node.Retrieve();
        activeComponentField = null; componentEditStart = null;
        if (Equals(before, after)) return;
        var beforeRevision = revision; var afterRevision = ++nextRevision;
        using (CreateDocumentTransaction())
        {
            RecordOperation(new MemberOperation(field.Node, before, after, () => revision = beforeRevision, () => revision = afterRevision));
            revision = afterRevision;
        }
        LastAction = "Edit " + field.Component.GetType().Name + ": " + field.Label;
    }
    internal void ReleaseComponentMutationCleanup()
    { foreach (var mutation in componentMutationCleanup.ToArray()) { mutation.Dispose(); componentMutationCleanup.Remove(mutation); } }
    public void EndEdits() { EndComponentVirtualEdit(); EndTransformEdit(); EndComponentEdit(); EndCollectionEdit(); EndCommonPropertyEdit(); EndMultiTransformEdit(); EndSceneOffsetEdit(); }
    private sealed class MemberOperation(IMemberNode node, object? before, object? after, Action undoRevision, Action redoRevision) : Operation
    {
        protected override void Rollback() { node.Update(before); undoRevision(); }
        protected override void Rollforward() { node.Update(after); redoRevision(); }
    }
}
