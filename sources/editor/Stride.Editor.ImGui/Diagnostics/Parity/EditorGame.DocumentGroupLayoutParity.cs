namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private Func<bool>? pendingDocumentGroupLayoutParityRestore;
    private TaskCompletionSource<bool>? documentGroupLayoutParityCompletion;

    private string ValidateDocumentGroupParityLayoutPath(string path)
    {
        var option = Array.IndexOf(args, "--engine-capability");
        if (!Controls.Enabled || !args.Contains("--ui-test") || option < 0 || option + 2 >= args.Length
            || args[option + 1] != "E05.groups-layout")
            throw new InvalidOperationException("Document layout inspection requires its private capability fixture.");
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(args[option + 2]), StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path) != "document-groups-layout.ini")
            throw new InvalidOperationException("Document layout inspection must use its own artifact file.");
        return path;
    }

    private void SaveDocumentGroupParityLayout(string path)
    {
        path = ValidateDocumentGroupParityLayoutPath(path);
        if (!DocumentGroupParityCaseQuiescent) throw new InvalidOperationException("Document layout owners are busy.");
        SaveEditorLayout(path);
        var native = EditorDocumentGroupLayoutCodec.Extract(File.ReadAllText(path), out var groups);
        if (groups == null || groups.Workspace != DocumentGroupWorkspaceIdentity || !native.Contains("[Window][", StringComparison.Ordinal))
            throw new InvalidDataException("Production layout save did not retain actual document membership.");
    }

    private Task<bool> QueueDocumentGroupParityLayoutRestore(string path)
    {
        path = ValidateDocumentGroupParityLayoutPath(path);
        if (pendingDocumentGroupLayoutParityRestore != null || !DocumentGroupParityCaseQuiescent)
            throw new InvalidOperationException("Document layout restore owners are busy.");
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Document layout is too large.");
        var layout = File.ReadAllText(path);
        EditorDocumentGroupLayoutCodec.Extract(layout, out var groups);
        if (groups == null || groups.Workspace != DocumentGroupWorkspaceIdentity)
            throw new InvalidDataException("Document layout belongs to another workspace.");
        var expectedWorkspace = workspace;
        var expectedHistory = sessionHistory;
        var owners = documents.Entries.Select(entry => (Entry: entry, Document: entry.Document, State: entry.State)).ToArray();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        documentGroupLayoutParityCompletion = completion;
        pendingDocumentGroupLayoutParityRestore = () =>
        {
            if (!ReferenceEquals(workspace, expectedWorkspace) || !ReferenceEquals(sessionHistory, expectedHistory)
                || !documents.Entries.SequenceEqual(owners.Select(owner => owner.Entry))
                || owners.Any(owner => !ReferenceEquals(owner.Entry.Document, owner.Document) || !ReferenceEquals(owner.Entry.State, owner.State)))
                throw new InvalidOperationException("Document layout restore lost its retained owners.");
            if (!DocumentGroupParityCaseQuiescent) return false;
            if (!LoadEditorLayoutFromMemory(layout, groups)) throw new InvalidDataException("Production layout restore failed.");
            RestoreCurrentDocumentGroupLayout();
            if (restoredDocumentGroupLayout != null) throw new InvalidOperationException("Document layout membership was not adopted.");
            completion.TrySetResult(true);
            return true;
        };
        return completion.Task;
    }

    private void PumpDocumentGroupParityLayoutRestoreBeforeFrame()
    {
        if (pendingDocumentGroupLayoutParityRestore == null) return;
        try
        {
            if (!pendingDocumentGroupLayoutParityRestore()) return;
        }
        catch (Exception error) { documentGroupLayoutParityCompletion?.TrySetException(error); }
        pendingDocumentGroupLayoutParityRestore = null;
        documentGroupLayoutParityCompletion = null;
    }

    private void CancelDocumentGroupParityLayoutRestore()
    {
        pendingDocumentGroupLayoutParityRestore = null;
        documentGroupLayoutParityCompletion?.TrySetCanceled();
        documentGroupLayoutParityCompletion = null;
    }
}
