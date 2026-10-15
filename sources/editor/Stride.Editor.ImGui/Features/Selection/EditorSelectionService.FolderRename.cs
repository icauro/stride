namespace Stride.Editor.ImGui;

internal sealed partial class EditorSelectionService
{
    internal void RenameHierarchyFolderSelection(EntityHierarchyDocument owner, Guid? parent, string previous, string replacement)
    {
        using var identity = context.PreserveNativeSelectionIdentity();
        HierarchyContentItem Remap(HierarchyContentItem item)
        {
            if (!ReferenceEquals(item.Document, owner) || item.FolderOwner != parent || item.FolderPath is not { } path
                || !path.Equals(previous, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(previous + "/", StringComparison.OrdinalIgnoreCase)) return item;
            return item with { FolderPath = replacement + path[previous.Length..] };
        }
        var next = hierarchyContent.Select(Remap).ToArray();
        var changed = !next.SequenceEqual(hierarchyContent);
        using var change = BeforeSelectionChange(changed);
        for (var index = 0; index < next.Length; index++) hierarchyContent[index] = next[index];
        for (var index = 0; index < hierarchySelectionOrder.Count; index++)
            if (hierarchySelectionOrder[index] is HierarchyContentItem selected) hierarchySelectionOrder[index] = Remap(selected);
        if (primaryContent is { } primary) primaryContent = Remap(primary);
        if (contentAnchor is { } anchor) contentAnchor = Remap(anchor);
        if (hierarchyRangeAnchor is HierarchyContentItem range) hierarchyRangeAnchor = Remap(range);
        if (changed) { contentProjectionVersions.Clear(); ContentVersion++; }
    }
}
