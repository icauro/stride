using Stride.Engine;

namespace Stride.Editor.ImGui;

internal readonly record struct EntitySelectionSnapshot(Guid[] Ids, Guid? PrimaryId);

internal abstract partial class EntityHierarchyDocument
{
    private readonly List<Guid> selectedEntityIds = [];
    private readonly HashSet<Guid> selectedEntitySet = [];
    private Entity? primarySelected;
    public IReadOnlyList<Guid> SelectedIds => selectedEntityIds;
    public int SelectedCount => selectedEntityIds.Count;
    public long SelectionVersion { get; private set; }
    public event Action? SelectionChanged;
    public bool IsSelected(Guid id) => selectedEntitySet.Contains(id);
    public EntitySelectionSnapshot SnapshotSelection() => new(selectedEntityIds.ToArray(), primarySelected?.Id);

    public void SelectEntity(Entity? entity, bool toggle = false)
    {
        if (entity != null && (!Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var part) || !ReferenceEquals(part.Entity, entity)))
            throw new ArgumentException("The entity does not belong to this scene.", nameof(entity));
        if (toggle && entity == null) return;
        if (!toggle)
        {
            if (selectedEntityIds.Count == (entity == null ? 0 : 1) && primarySelected == entity) return;
            BeforeCanonicalSelectionChange();
            EndEdits(); selectedEntityIds.Clear(); selectedEntitySet.Clear();
            if (entity != null) { selectedEntityIds.Add(entity.Id); selectedEntitySet.Add(entity.Id); }
            primarySelected = entity;
        }
        else
        {
            BeforeCanonicalSelectionChange();
            EndEdits();
            if (selectedEntitySet.Remove(entity!.Id)) selectedEntityIds.Remove(entity.Id);
            else { selectedEntitySet.Add(entity.Id); selectedEntityIds.Add(entity.Id); }
            primarySelected = selectedEntitySet.Contains(entity.Id) ? entity : FindSelectedPrimary();
        }
        NotifySelectionChanged();
    }

    public void SelectRange(IEnumerable<Guid> ids, Guid primary, bool add = false)
    {
        var range = ids.Where(Asset.Hierarchy.Parts.ContainsKey).Distinct().ToArray();
        RestoreSelection(add ? selectedEntityIds.Concat(range) : range, primary);
    }

    public void RestoreSelection(IEnumerable<Guid> ids, Guid? primary)
    {
        var next = ids.Where(Asset.Hierarchy.Parts.ContainsKey).Distinct().ToArray();
        var primaryId = primary.HasValue && next.Contains(primary.Value) ? primary : next.Length == 0 ? null : (Guid?)next[^1];
        if (selectedEntityIds.SequenceEqual(next) && primarySelected?.Id == primaryId) return;
        BeforeCanonicalSelectionChange();
        EndEdits(); selectedEntityIds.Clear(); selectedEntityIds.AddRange(next);
        selectedEntitySet.Clear(); selectedEntitySet.UnionWith(next);
        primarySelected = primaryId is { } id ? Asset.Hierarchy.Parts[id].Entity : null;
        NotifySelectionChanged();
    }

    internal void PruneSelection() => RestoreSelection(selectedEntityIds, primarySelected?.Id);
    private void BeforeCanonicalSelectionChange()
    {
        if (SessionHistory is { IsCompensating: false } history && history.Registry.Find(Asset.Id) is { IsPublished: true } state
            && ReferenceEquals(state.Document, this))
            history.BeforeSelectionChange?.Invoke();
    }
    private Entity? FindSelectedPrimary() => selectedEntityIds.Count == 0 ? null : Asset.Hierarchy.Parts[selectedEntityIds[^1]].Entity;
    private void NotifySelectionChanged() { SelectionVersion++; SelectionChanged?.Invoke(); }
}
