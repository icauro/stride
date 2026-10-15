namespace Stride.Editor.ImGui;

internal enum EditorDocumentGroupCommand { Float, Dock, Horizontal, Vertical, Next, Previous }

internal sealed class EditorDocumentGroup(Guid id)
{
    internal Guid Id { get; } = id;
    internal List<EditorDocumentEntry> Items { get; } = [];
    internal EditorDocumentEntry? Active { get; set; }
    internal bool Floating { get; set; }
    internal string WindowName => Id == EditorDocumentGroups.PrimaryId ? "Scene" : (Active?.Name ?? "") + "###document-group/" + Id;
}

internal sealed record EditorDocumentGroupLayout(Guid Id, bool Floating, string[] Paths, string? ActivePath);
internal sealed record EditorDocumentGroupsLayout(int Version, string Workspace, EditorDocumentGroupLayout[] Groups, string? ActivePath = null);
internal readonly record struct EditorDocumentGroupMove(EditorDocumentEntry Entry, Guid Source, Guid Destination,
    EditorDocumentGroupCommand Command, IEditorDocument Document, EditorAuthoringState? State);
internal readonly record struct EditorDocumentGroupNeighbors(Guid Previous, Guid Next);

/// <summary>Presentation membership only; never creates, releases or edits authored owners.</summary>
internal sealed class EditorDocumentGroups
{
    internal static readonly Guid PrimaryId = new("a44e899e-780a-43c4-b371-2e5972f292c8");
    private readonly List<EditorDocumentGroup> groups = [new(PrimaryId)];
    private readonly Dictionary<Guid, EditorDocumentGroup> membership = [];
    private readonly HashSet<EditorDocumentEntry> openEntries = [];
    private readonly Dictionary<Guid, EditorDocumentGroupNeighbors> neighbors = [];
    private readonly HashSet<Guid> topologyIds = [];
    internal IReadOnlyList<EditorDocumentGroup> Groups => groups;
    internal EditorDocumentGroup Primary => Find(PrimaryId)!;
    internal EditorDocumentGroup? Find(Guid id) => groups.Find(group => group.Id == id);
    internal EditorDocumentGroup? Find(EditorDocumentEntry entry) => membership.GetValueOrDefault(entry.Id);

    internal void Synchronize(IReadOnlyList<EditorDocumentEntry> entries)
    {
        openEntries.Clear();
        foreach (var entry in entries) openEntries.Add(entry);
        for (var groupIndex = groups.Count - 1; groupIndex >= 0; groupIndex--)
        {
            var group = groups[groupIndex];
            for (var index = group.Items.Count - 1; index >= 0; index--)
            {
                var entry = group.Items[index];
                if (openEntries.Contains(entry)) continue;
                membership.Remove(entry.Id); group.Items.RemoveAt(index);
                if (ReferenceEquals(group.Active, entry))
                    group.Active = group.Items.Count == 0 ? null : group.Items[Math.Min(index, group.Items.Count - 1)];
            }
            if (group.Id != PrimaryId && group.Items.Count == 0) groups.RemoveAt(groupIndex);
        }
        foreach (var entry in entries)
            if (!membership.ContainsKey(entry.Id))
            { Primary.Items.Add(entry); membership.Add(entry.Id, Primary); Primary.Active ??= entry; }
    }

    internal void Select(EditorDocumentEntry entry)
    {
        var group = Find(entry) ?? throw new InvalidOperationException("The document view is no longer grouped.");
        group.Active = entry;
    }

    internal void Merge(EditorDocumentGroup source, EditorDocumentGroup destination, bool selectSource)
    {
        if (ReferenceEquals(source, destination) || !groups.Contains(source) || !groups.Contains(destination)
            || source.Id == PrimaryId) throw new InvalidOperationException("Invalid document pane merge.");
        foreach (var entry in source.Items) { destination.Items.Add(entry); membership[entry.Id] = destination; }
        if (selectSource && source.Active != null) destination.Active = source.Active;
        destination.Active ??= source.Active;
        source.Items.Clear(); source.Active = null; groups.Remove(source);
    }

    internal bool CanMove(EditorDocumentEntry entry, EditorDocumentGroupCommand command)
    {
        var source = Find(entry);
        if (source == null) return false;
        return command switch
        {
            EditorDocumentGroupCommand.Float => !source.Floating || source.Items.Count > 1,
            EditorDocumentGroupCommand.Dock => source.Floating,
            EditorDocumentGroupCommand.Horizontal or EditorDocumentGroupCommand.Vertical => source.Items.Count > 1,
            EditorDocumentGroupCommand.Next => Adjacent(source, 1) != null,
            EditorDocumentGroupCommand.Previous => Adjacent(source, -1) != null,
            _ => false,
        };
    }

    private EditorDocumentGroup? Adjacent(EditorDocumentGroup source, int direction)
    {
        var adjacent = neighbors.GetValueOrDefault(source.Id);
        return Find(direction > 0 ? adjacent.Next : adjacent.Previous);
    }

    internal bool ApplyNativeTopology(IReadOnlyList<Guid> order, IReadOnlyDictionary<Guid, EditorDocumentGroupNeighbors> adjacent)
    {
        topologyIds.Clear();
        if (order.Count != groups.Count) throw new InvalidOperationException("Native group order must contain each actual view group exactly once.");
        foreach (var id in order)
            if (!topologyIds.Add(id) || Find(id) == null)
                throw new InvalidOperationException("Native group order must contain each actual view group exactly once.");
        neighbors.Clear();
        foreach (var item in adjacent)
            if (Find(item.Key) != null) neighbors.Add(item.Key, item.Value);
        var changed = false;
        for (var index = 0; index < order.Count; index++)
        {
            if (groups[index].Id == order[index]) continue;
            var next = Find(order[index])!;
            groups.Remove(next); groups.Insert(index, next); changed = true;
        }
        return changed;
    }

    internal EditorDocumentGroupMove Plan(EditorDocumentEntry entry, EditorDocumentGroupCommand command)
    {
        if (!CanMove(entry, command)) throw new InvalidOperationException("The document group command is unavailable.");
        var source = Find(entry)!;
        var target = command switch
        {
            EditorDocumentGroupCommand.Next => Adjacent(source, 1)!.Id,
            EditorDocumentGroupCommand.Previous => Adjacent(source, -1)!.Id,
            EditorDocumentGroupCommand.Dock => (groups.FirstOrDefault(group => !group.Floating) ?? Primary).Id,
            _ => Guid.NewGuid(),
        };
        return new(entry, source.Id, target, command, entry.Document, entry.State);
    }

    internal void Apply(EditorDocumentGroupMove move)
    {
        var source = Find(move.Entry);
        if (source == null || source.Id != move.Source || !ReferenceEquals(move.Entry.Document, move.Document)
            || !ReferenceEquals(move.Entry.State, move.State) || !CanMove(move.Entry, move.Command))
            throw new InvalidOperationException("The document group changed before the command was applied.");
        var target = Find(move.Destination);
        var expected = move.Command switch
        {
            EditorDocumentGroupCommand.Next => Adjacent(source, 1),
            EditorDocumentGroupCommand.Previous => Adjacent(source, -1),
            EditorDocumentGroupCommand.Dock => groups.FirstOrDefault(group => !group.Floating) ?? Primary,
            _ => null,
        };
        if (expected != null && expected.Id != move.Destination)
            throw new InvalidOperationException("The destination document group changed.");
        if (target == null)
        {
            target = new(move.Destination) { Floating = move.Command == EditorDocumentGroupCommand.Float || source.Floating };
            groups.Insert(groups.IndexOf(source) + 1, target);
        }
        var index = source.Items.IndexOf(move.Entry);
        source.Items.RemoveAt(index);
        if (ReferenceEquals(source.Active, move.Entry))
            source.Active = source.Items.Count == 0 ? null : source.Items[Math.Min(index, source.Items.Count - 1)];
        if (move.Command is EditorDocumentGroupCommand.Next or EditorDocumentGroupCommand.Previous) target.Items.Insert(0, move.Entry);
        else target.Items.Add(move.Entry);
        target.Active = move.Entry; membership[move.Entry.Id] = target;
        if (move.Command == EditorDocumentGroupCommand.Dock) target.Floating = false;
        if (source.Id != PrimaryId && source.Items.Count == 0) groups.Remove(source);
    }

    internal void ApplyOrder(EditorDocumentGroup group, IReadOnlyList<(EditorDocumentEntry Entry, int Order)> order)
    {
        if (!groups.Contains(group) || order.Count != group.Items.Count) return;
        var sorted = order.OrderBy(item => item.Order).Select(item => item.Entry).ToArray();
        if (sorted.Distinct().Count() != group.Items.Count || sorted.Any(entry => !group.Items.Contains(entry)))
            throw new InvalidOperationException("Group order must contain exactly its current document views.");
        for (var index = 0; index < sorted.Length; index++) group.Items[index] = sorted[index];
    }

    internal EditorDocumentGroupsLayout Capture(string workspace, string? activePath = null) => new(1, workspace,
        groups.Select(group => new EditorDocumentGroupLayout(group.Id, group.Floating,
            group.Items.Select(entry => entry.Path).ToArray(), group.Active?.Path)).ToArray(), activePath);

    internal void Restore(EditorDocumentGroupsLayout layout, string workspace, IReadOnlyList<EditorDocumentEntry> entries)
    {
        if (layout.Version != 1 || !string.Equals(layout.Workspace, workspace, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Document-group layout belongs to another workspace or version.");
        if (layout.Groups == null || layout.Groups.Length == 0 || !layout.Groups.Any(group => group.Id == PrimaryId)
            || layout.Groups.Select(group => group.Id).Distinct().Count() != layout.Groups.Length
            || layout.Groups.Any(group => group.Id == Guid.Empty || group.Paths == null))
            throw new InvalidOperationException("Invalid document-group identities.");
        if (layout.ActivePath != null && !layout.Groups.Any(group => group.Paths.Contains(layout.ActivePath, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The active document is not a member of the saved layout.");
        var restored = new List<EditorDocumentGroup>();
        var assigned = new HashSet<Guid>();
        foreach (var saved in layout.Groups)
        {
            var group = new EditorDocumentGroup(saved.Id) { Floating = saved.Floating };
            foreach (var path in saved.Paths)
            {
                var entry = entries.FirstOrDefault(entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase));
                if (entry == null) continue; // Layout never opens stale assets or creates owners.
                if (!assigned.Add(entry.Id)) throw new InvalidOperationException("A document occurs in multiple saved groups.");
                group.Items.Add(entry);
            }
            group.Active = group.Items.Find(entry => string.Equals(entry.Path, saved.ActivePath, StringComparison.OrdinalIgnoreCase))
                ?? group.Items.FirstOrDefault();
            if (group.Id == PrimaryId || group.Items.Count != 0) restored.Add(group);
        }
        groups.Clear(); groups.AddRange(restored); membership.Clear(); neighbors.Clear();
        foreach (var group in groups) foreach (var entry in group.Items) membership.Add(entry.Id, group);
        Synchronize(entries);
    }

    internal void Clear() { groups.Clear(); groups.Add(new(PrimaryId)); membership.Clear(); openEntries.Clear(); neighbors.Clear(); topologyIds.Clear(); }
}
