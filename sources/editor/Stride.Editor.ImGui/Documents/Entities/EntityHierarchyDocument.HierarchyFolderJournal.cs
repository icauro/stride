namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    private void RecordHierarchyFolderGroup(Action mutate, string title)
    {
        if (SessionHistory == null || SessionHistory.IsCapturingOperations) { mutate(); return; }
        using var transaction = CreateDocumentTransaction(title);
        using var capture = SessionHistory.CaptureOperationsAtomic(mutate);
        capture.Publish(SessionHistory, title);
    }

    private void ChangeHierarchyFolderOwnerPaths(FolderOwnerPathsChange[] changes, string title)
    {
        var beforeRevision = revision; var afterRevision = ++nextRevision;
        void Apply(bool forward)
        {
            foreach (var change in changes)
                emptyHierarchyFolders[change.Owner] = new HashSet<string>(forward ? change.After : change.Before, StringComparer.OrdinalIgnoreCase);
            FolderVersion++; revision = forward ? afterRevision : beforeRevision;
        }
        try
        {
            Apply(true);
            using (CreateDocumentTransaction(title)) RecordOperation(new HierarchyCreationOperation(() => Apply(false), () => Apply(true)));
        }
        catch { Apply(false); throw; }
        LastAction = title;
    }
}
