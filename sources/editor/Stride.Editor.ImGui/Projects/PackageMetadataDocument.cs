using Stride.Core.Assets;
using Stride.Core.Yaml;
using System.Security.Cryptography;

namespace Stride.Editor.ImGui;

/// <summary>Ownership adapter for the actual session package metadata, rather than an asset or editor tab.</summary>
internal sealed partial class PackageMetadataDocument : IEditorDocument, ISessionAuthoringDocument
{
    private readonly ProjectWorkspace workspace;
    private readonly Package package;
    private readonly EditorDocumentHistoryScope historyScope;
    private long revision, nextRevision, savedRevision;
    private byte[] savedMetadata;
    private bool initiallyDirty;
    public PackageMetadataDocument(ProjectWorkspace workspace, Package package, EditorSessionHistory history)
    {
        this.workspace = workspace; this.package = package; historyScope = new(this, history, "Edit package");
        initiallyDirty = package.IsDirty; savedMetadata = CaptureMetadata();
        RememberRecoveryDiskBaseline();
    }
    public string Path => package.FullPath.ToOSPath();
    public bool IsDirty => initiallyDirty || revision != savedRevision || !CaptureMetadata().AsSpan().SequenceEqual(savedMetadata);
    public long Revision => revision;
    public long AllocateRevision() => ++nextRevision;
    public void RestoreRevision(long value) { revision = value; package.IsDirty = true; }
    public bool TryReconcileSavedState()
    {
        if (IsDirty) return false;
        package.IsDirty = false;
        return true;
    }
    private byte[] CaptureMetadata()
    {
        using var stream = new MemoryStream();
        AssetYamlSerializer.Default.Serialize(stream, package);
        return SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
    }
    public bool CanSave => !package.IsReadOnly;
    public bool CanUndo => SessionHistory?.CanUndo == true;
    public bool CanRedo => SessionHistory?.CanRedo == true;
    public EditorSessionHistory? SessionHistory => historyScope.SessionHistory;
    public void BindAuthoringState(EditorAuthoringState state) => historyScope.Bind(state);
    public void CommitEdits() { }
    public void Save() => throw new InvalidOperationException("Save package metadata through the editor's guarded package save.");
    public void Undo() => SessionHistory?.Undo();
    public void Redo() => SessionHistory?.Redo();
    public void Dispose() { historyScope.EnsureMayDispose(); sessionMembership?.DisposeAdoptions(); }
}
