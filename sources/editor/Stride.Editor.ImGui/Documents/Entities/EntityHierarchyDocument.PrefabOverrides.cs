using Stride.Core.Assets.Quantum;
using Stride.Core.Transactions;
using Stride.Core.Reflection;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    private int suppressOwnScalarRecording;
    internal bool CanOverrideInheritedComponent(ComponentField field)
        => CanSave && SessionHistory != null && field.SupportsEditing && EditorHierarchyGraphs.IsScalar(field.Type)
        && historyScope.AuthoringState is { IsPublished: true } state && ReferenceEquals(SessionHistory.Registry.Find(Asset.Id), state)
        && field.Node is IAssetMemberNode { CanOverride: true, BaseNode: IAssetMemberNode baseMember } && !baseMember.GetContentOverride().HasFlag(OverrideType.Sealed)
        && field.Component.Entity is { } owner && Asset.Hierarchy.Parts.TryGetValue(owner.Id, out var design)
        && ReferenceEquals(design.Entity, owner) && owner.Components.Contains(field.Component)
        && (design.Base != null
            ? HierarchyGraph.Container.TryGetGraph(design.Base.BasePartAsset.Id) is Stride.Assets.Quantum.EntityHierarchyPropertyGraph baseGraph
                && baseGraph.Asset.Hierarchy.Parts.ContainsKey(design.Base.BasePartId)
            : ReferenceEquals(HierarchyGraph.Container.TryGetGraph(baseMember.PropertyGraph.AuthoredItem.Id), baseMember.PropertyGraph))
        && IsKnownComponentField(field) && CanEditHierarchyField(field);
    internal bool CanEditInheritedScalarBatch(IReadOnlyList<ComponentField> fields)
        => fields.Count != 0 && fields.Any(CanOverrideInheritedComponent)
        && fields.All(field => CanOverrideInheritedComponent(field) || CanEditOwnedScalarInInheritedBatch(field));
    internal bool CanResetInheritedScalarBatch(IReadOnlyList<ComponentField> fields) => CanEditInheritedScalarBatch(fields);
    private bool CanEditOwnedScalarInInheritedBatch(ComponentField field)
        => CanSave && SessionHistory != null && field.SupportsEditing && EditorHierarchyGraphs.IsScalar(field.Type)
        && historyScope.AuthoringState is { IsPublished: true } state && ReferenceEquals(SessionHistory.Registry.Find(Asset.Id), state)
        && field.Node is IAssetMemberNode { BaseNode: null }
        && field.Component.Entity is { } owner && owner.Components.Contains(field.Component)
        && IsKnownComponentField(field) && CanEditHierarchyField(field) && AssetMetadataDocument.CanAssignAuthoringNode(field.Node)
        && field.Presentation?.IsReadOnly(field) != true;
    internal void EditInheritedScalar(ComponentField field, object? value)
        => EditInheritedScalars([field], [value]);
    internal void ResetInheritedScalar(ComponentField field) => ResetInheritedScalars([field]);
    internal void EditInheritedScalars(IReadOnlyList<ComponentField> fields, IReadOnlyList<object?> values)
    {
        if (fields.Count != values.Count) throw new ArgumentException("Each inherited property requires one value.");
        ChangeInheritedScalars(fields, fields.Select((field, index) => field.NormalizeValue(values[index])).ToArray(), false);
    }
    internal void ResetInheritedScalars(IReadOnlyList<ComponentField> fields) => ChangeInheritedScalars(fields, new object?[fields.Count], true);
    private void ChangeInheritedScalars(IReadOnlyList<ComponentField> fields, IReadOnlyList<object?> values, bool reset)
    {
        EndEdits();
        if (fields.Count == 0 || (reset ? !CanResetInheritedScalarBatch(fields) : !CanEditInheritedScalarBatch(fields))
            || fields.Select(field => field.Node).Distinct().Count() != fields.Count)
            throw new InvalidOperationException("These inherited properties cannot be overridden together.");
        for (var index = 0; index < fields.Count; index++)
        {
            var field = fields[index]; var value = values[index];
            if (!reset && value == null && (field.Type.IsValueType && !field.IsNullable || !field.Type.IsValueType && !field.AllowsNullObject))
                throw new ArgumentException("This property cannot contain null.");
            if (!reset && value != null && !field.Type.IsInstanceOfType(value)) throw new ArgumentException("The override has an incompatible value.");
        }
        var title = (reset ? "Reset " : "Override ") + (fields.Count == 1 ? fields[0].Label : "properties");
        ApplyInheritedGraphMutation(() =>
        {
            if (reset ? !CanResetInheritedScalarBatch(fields) : !CanEditInheritedScalarBatch(fields))
                throw new InvalidOperationException("The inherited property owners changed.");
            for (var index = 0; index < fields.Count; index++)
            {
                var node = (IAssetMemberNode)fields[index].Node;
                if (reset)
                {
                    if (node.BaseNode is not IAssetMemberNode) continue;
                    node.OverrideContent(false); node.PropertyGraph.ReconcileScalarWithBase(node);
                }
                else
                {
                    var sealedFlag = node.GetContentOverride() & OverrideType.Sealed;
                    node.Update(values[index]); node.RestoreContentOverride(node.GetContentOverride() | sealedFlag);
                }
            }
        }, title);
    }
    private sealed record ScalarSnapshot(IAssetMemberNode Node, object? Value, OverrideType Override);
    private sealed class ScalarRestoreOperation(ScalarSnapshot[] before, Action restoreRevision) : Operation
    {
        protected override void Rollback() { foreach (var snapshot in before.Reverse()) RestoreScalar(snapshot); restoreRevision(); }
        protected override void Rollforward() => throw new InvalidOperationException("An incomplete initial mutation is only retained for recovery.");
    }
    private static void RestoreScalar(ScalarSnapshot snapshot) { snapshot.Node.Update(snapshot.Value); snapshot.Node.RestoreContentOverride(snapshot.Override); }
    private sealed class ScalarOverrideBatchOperation(ScalarSnapshot[] before, ScalarSnapshot[] after, Action undoRevision, Action redoRevision) : Operation
    {
        protected override void Rollback() => Apply(before, after, undoRevision);
        protected override void Rollforward() => Apply(after, before, redoRevision);
        private static void Apply(ScalarSnapshot[] desired, ScalarSnapshot[] original, Action revision)
        {
            var index = 0;
            try { for (; index < desired.Length; index++) RestoreScalar(desired[index]); revision(); }
            catch (Exception failure)
            {
                var failures = new List<Exception> { failure };
                for (var completed = Math.Min(index, original.Length - 1); completed >= 0; completed--)
                    try { RestoreScalar(original[completed]); } catch (Exception recovery) { failures.Add(recovery); }
                if (failures.Count > 1) throw new AggregateException("Inherited property replay and compensation failed.", failures);
                throw;
            }
        }
    }
    internal void RecordPropagatedScalar(IAssetMemberNode node, AssetMemberNodeChangeEventArgs args)
    {
        if (IsCapturingPrefabExtraction || suppressOwnScalarRecording != 0 || Equals(args.OldValue, args.NewValue) && args.PreviousOverride == args.NewOverride) return;
        if (historyScope.AuthoringState is not { } owner) throw new InvalidOperationException("Bind a propagated graph before authoring it.");
        var beforeRevision = revision; var afterRevision = ++nextRevision;
        var operation = new ScalarOverrideBatchOperation(
            [new ScalarSnapshot(node, args.OldValue, args.PreviousOverride)], [new ScalarSnapshot(node, args.NewValue, args.NewOverride)],
            () => revision = beforeRevision, () => revision = afterRevision);
        SessionHistory!.PushOperation(operation, owner); revision = afterRevision;
    }
}
