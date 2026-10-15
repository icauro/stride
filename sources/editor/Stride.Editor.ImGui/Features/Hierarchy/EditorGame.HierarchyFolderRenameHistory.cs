using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void RenameHierarchyFolderWithSelection(EntityHierarchyDocument owner, Guid? parent, string previous, string name,
        string replacement, EditorAuthoringState participant)
    {
        var history = sessionHistory ?? throw new InvalidOperationException("The folder has no canonical history.");
        void Apply()
        {
            owner.RenameHierarchyFolder(parent, previous, name);
            var operation = new HierarchyFolderSelectionRenameOperation(this, owner, parent, previous, replacement);
            operation.ApplyInitial();
            try { history.PushOperation(operation, participant); }
            catch { operation.CancelInitial(); throw; }
        }
        if (history.IsCapturingOperations) Apply();
        else
        {
            using var captured = history.CaptureOperationsAtomic(Apply);
            captured.Publish(history, "Rename folder");
        }
    }
    private void RemapHierarchyFolderPresentation(EntityHierarchyDocument owner, Guid? parent, string previous, string replacement)
    {
        SelectionService.RenameHierarchyFolderSelection(owner, parent, previous, replacement);
        string Remap(string path) => path.Equals(previous, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(previous + "/", StringComparison.OrdinalIgnoreCase) ? replacement + path[previous.Length..] : path;
        if (ReferenceEquals(folderSelectionDocument, owner) && selectedHierarchyFolder is { } selected && selected.Owner == parent)
            selectedHierarchyFolder = (parent, Remap(selected.Path));
        if (ReferenceEquals(document, owner) && revealHierarchyFolder is { } reveal && reveal.Owner == parent)
            revealHierarchyFolder = (parent, Remap(reveal.Path));
    }
    private sealed class HierarchyFolderSelectionRenameOperation(EditorGame editor, EntityHierarchyDocument owner, Guid? parent,
        string previous, string replacement) : Operation
    {
        private WeakReference<EditorGame>? host = new(editor);
        private EntityHierarchyDocument? document = owner;
        internal void ApplyInitial() => Apply(previous, replacement);
        internal void CancelInitial() => Apply(replacement, previous);
        protected override void Rollback() => Apply(replacement, previous);
        protected override void Rollforward() => Apply(previous, replacement);
        private void Apply(string source, string target)
        {
            if (document != null && host?.TryGetTarget(out var targetHost) == true)
                targetHost.RemapHierarchyFolderPresentation(document, parent, source, target);
        }
        protected override void FreezeContent() { host = null; document = null; }
    }
}
