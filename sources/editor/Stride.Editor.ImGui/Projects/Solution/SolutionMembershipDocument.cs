using System.Security.Cryptography;
using Stride.Core.Assets;
using Stride.Core.Solutions;

namespace Stride.Editor.ImGui;

internal sealed partial class SolutionMembershipDocument : IEditorDocument, ISessionAuthoringDocument
{
    private readonly ProjectWorkspace workspace;
    private readonly EditorDocumentHistoryScope history;
    private byte[] diskHash;
    private long revision, nextRevision, savedRevision;
    internal SolutionMembershipDocument(ProjectWorkspace workspace, EditorSessionHistory session)
    {
        this.workspace = workspace; history = new(this, session, "Edit solution");
        if (string.IsNullOrEmpty(workspace.Session.SolutionPath?.ToOSPath())) throw new InvalidOperationException("Open a solution before adding a project.");
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        diskHash = SHA256.HashData(stream);
    }
    public string Path => workspace.Session.SolutionPath.ToOSPath();
    public bool IsDirty => revision != savedRevision;
    public bool CanSave => !string.IsNullOrEmpty(workspace.Session.SolutionPath?.ToOSPath());
    public bool CanUndo => SessionHistory!.CanUndo;
    public bool CanRedo => SessionHistory!.CanRedo;
    public EditorSessionHistory? SessionHistory => history.SessionHistory;
    internal long Revision => revision;
    public void BindAuthoringState(EditorAuthoringState state) => history.Bind(state);
    public void CommitEdits() { }
    public void Undo() => SessionHistory!.Undo();
    public void Redo() => SessionHistory!.Redo();
    internal void ReleaseRetainedStates() => ReleaseRemovedProjects();
    public void Save() => Save(workspace.Session.AuthoringSolution);
    internal void Save(Solution solution)
    {
        history.EnsureMaySave();
        if (!ReferenceEquals(solution, workspace.Session.AuthoringSolution)) throw new InvalidOperationException("The solution owner changed.");
        var temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, ".stride-solution-" + Guid.NewGuid().ToString("N") + System.IO.Path.GetExtension(Path));
        try
        {
            // Native serialization retains existing configuration/global sections,
            // folder/project IDs and the actual .sln versus .slnx format.
            solution.SaveAs(temporary);
            var bytes = File.ReadAllBytes(temporary);
            diskHash = WindowsOwnedAssetFileWriter.Write(Path, diskHash, bytes).ContentHash;
            savedRevision = revision;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Dispose()
    {
        history.EnsureMayDispose();
        removalMembership?.DisposeAdoptions();
    }
}
