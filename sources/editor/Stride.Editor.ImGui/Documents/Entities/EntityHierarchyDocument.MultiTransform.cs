using Stride.Core.Transactions;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    private Entity[]? transformSelection;
    private TransformState[]? transformSelectionStart;
    private TransformState[]? transformSelectionObserved;
    private TransformState[]? preparedMultiTransformAfter;
    private long multiTransformSynchronizationVersion;
    internal bool IsEditingMultiTransform => transformSelection != null;
    internal Action? CompleteMultiTransformGroup { get; set; }
    internal long MultiTransformSynchronizationVersion
    {
        get
        {
            if (transformSelection == null) return multiTransformSynchronizationVersion;
            var changed = false;
            for (var index = 0; index < transformSelection.Length; index++)
            {
                var state = TransformState.Read(transformSelection[index]);
                if (state == transformSelectionObserved![index]) continue;
                transformSelectionObserved[index] = state;
                changed = true;
            }
            if (changed) multiTransformSynchronizationVersion++;
            return multiTransformSynchronizationVersion;
        }
    }
    internal bool HasActiveMultiTransformChange
    {
        get
        {
            if (transformSelection == null) return false;
            for (var index = 0; index < transformSelection.Length; index++)
                if (TransformState.Read(transformSelection[index]) != transformSelectionStart![index]) return true;
            return false;
        }
    }
    internal bool CanTransformSelection => CanSave && SelectedCount > 0 && (SessionHistory == null
        ? SelectedIds.All(id => Asset.Hierarchy.Parts.TryGetValue(id, out var part) && part.Base == null)
        : ReferenceEquals(SessionHistory.Registry.Find(Asset.Id)?.Document, this)
            && GetSelectedTransformRoots().All(CanTransformCanonicalRoot));

    internal bool CanTransformSubset(IReadOnlyCollection<Entity> roots) => CanSave && roots.Count > 0
        && roots.All(entity => IsSelected(entity.Id) && Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var part)
            && ReferenceEquals(part.Entity, entity) && (SessionHistory == null ? part.Base == null : CanTransformCanonicalRoot(entity)))
        && (SessionHistory == null || ReferenceEquals(SessionHistory.Registry.Find(Asset.Id)?.Document, this));

    private bool CanTransformCanonicalRoot(Entity entity)
    {
        if (!Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var part) || !ReferenceEquals(part.Entity, entity)) return false;
        var fields = GetComponentFields(entity.Transform);
        foreach (var name in TransformMemberNames)
        {
            var field = fields.FirstOrDefault(candidate => candidate.Node.Name == name);
            if (field == null || !CanEditAggregateProperty(field) || !CanEditHierarchyField(field)) return false;
        }
        return true;
    }
    private static readonly string[] TransformMemberNames = [nameof(TransformComponent.Position), nameof(TransformComponent.Rotation), nameof(TransformComponent.Scale)];

    internal Entity[] GetSelectedTransformRoots()
    {
        var roots = new List<Entity>();
        foreach (var id in SelectedIds)
        {
            if (!Asset.Hierarchy.Parts.TryGetValue(id, out var part)) continue;
            var parent = part.Entity.Transform.Parent;
            while (parent != null && !IsSelected(parent.Entity.Id)) parent = parent.Parent;
            if (parent == null) roots.Add(part.Entity);
        }
        return roots.ToArray();
    }

    internal void BeginMultiTransformEdit(Entity[]? requestedRoots = null)
    {
        if (transformSelection != null) return;
        EndEdits();
        var roots = requestedRoots ?? GetSelectedTransformRoots();
        if (requestedRoots == null && !CanTransformSelection) throw new InvalidOperationException("The selected entities cannot be transformed.");
        if (!CanTransformSubset(roots)) throw new InvalidOperationException("The selected entities cannot be transformed.");
        if (roots.Distinct().Count() != roots.Length || roots.Any(entity => roots.Any(parent => !ReferenceEquals(entity, parent) && IsTransformAncestor(parent, entity))))
            throw new InvalidOperationException("The transform roots must be normalized.");
        transformSelection = (Entity[])roots.Clone();
        transformSelectionStart = transformSelection.Select(TransformState.Read).ToArray();
        transformSelectionObserved = (TransformState[])transformSelectionStart.Clone();
        multiTransformSynchronizationVersion++;
    }

    private static bool IsTransformAncestor(Entity parent, Entity child)
    {
        for (var transform = child.Transform.Parent; transform != null; transform = transform.Parent)
            if (ReferenceEquals(transform.Entity, parent)) return true;
        return false;
    }

    internal void EndMultiTransformEdit()
    {
        if (CompleteMultiTransformGroup is { } complete) { complete(); return; }
        if (transformSelection == null) return;
        var entities = transformSelection; var before = transformSelectionStart!;
        var changed = preparedMultiTransformAfter != null
            ? preparedMultiTransformAfter.Where((state, index) => state != before[index]).Any()
            : HasActiveMultiTransformChange;
        var preparedAfter = preparedMultiTransformAfter; preparedMultiTransformAfter = null;
        transformSelection = null; transformSelectionStart = null; transformSelectionObserved = null;
        multiTransformSynchronizationVersion++;
        if (!changed) return;
        var after = preparedAfter ?? entities.Select(TransformState.Read).ToArray();
        if (SessionHistory != null)
        {
            // Live engine gestures are authored drafts. Restore their starting TRS
            // before native node updates so Quantum captures the original value,
            // override flags and propagated derived changes rather than the draft.
            if (preparedAfter == null)
                for (var index = 0; index < entities.Length; index++) before[index].Apply(entities[index]);
            CommitCanonicalMultiTransform(entities, before, after);
            return;
        }
        if (preparedAfter != null)
            for (var index = 0; index < entities.Length; index++) after[index].Apply(entities[index]);
        var beforeRevision = revision; var afterRevision = ++nextRevision;
        try
        {
            using (CreateDocumentTransaction())
                RecordOperation(new MultiTransformOperation(entities, before, after,
                    () => revision = beforeRevision, () => revision = afterRevision));
        }
        catch
        {
            for (var index = 0; index < entities.Length; index++) before[index].Apply(entities[index]);
            revision = beforeRevision; throw;
        }
        revision = afterRevision;
        LastAction = entities.Length == 1 ? $"Edit transform: {entities[0].Name}" : "Transform entities";
    }

    internal void PrepareMultiTransformCommit()
    {
        if (transformSelection == null || preparedMultiTransformAfter != null) return;
        preparedMultiTransformAfter = transformSelection.Select(TransformState.Read).ToArray();
        for (var index = 0; index < transformSelection.Length; index++) transformSelectionStart![index].Apply(transformSelection[index]);
        multiTransformSynchronizationVersion++;
    }

    internal void CancelMultiTransformEdit()
    {
        if (transformSelection == null) return;
        for (var index = 0; index < transformSelection.Length; index++)
            transformSelectionStart![index].Apply(transformSelection[index]);
        transformSelection = null; transformSelectionStart = null; transformSelectionObserved = null; preparedMultiTransformAfter = null;
        multiTransformSynchronizationVersion++;
    }

    private sealed class MultiTransformOperation(Entity[] entities, TransformState[] before, TransformState[] after,
        Action undoRevision, Action redoRevision) : Operation
    {
        protected override void Rollback() { Apply(before); undoRevision(); }
        protected override void Rollforward() { Apply(after); redoRevision(); }
        private void Apply(TransformState[] states)
        {
            for (var index = 0; index < entities.Length; index++) states[index].Apply(entities[index]);
        }
    }
}
