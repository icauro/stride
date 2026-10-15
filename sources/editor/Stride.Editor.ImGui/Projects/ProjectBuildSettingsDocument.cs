using System.Security.Cryptography;
using Stride.Core.Assets;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal enum ProjectGraphicsApi { Default, Direct3D11, Direct3D12, Vulkan }

/// <summary>Explicit project properties on the actual session authoring stack.</summary>
internal sealed partial class ProjectBuildSettingsDocument : IEditorDocument, ISessionAuthoringDocument
{
    public const string GraphicsProperty = "StrideGraphicsApi";
    private readonly SolutionProject project;
    private readonly EditorDocumentHistoryScope history;
    private readonly AuthoredProjectProperties properties;
    private byte[] savedDiskHash;
    private string? graphicsRaw, containsRaw;
    private long revision, nextRevision, savedRevision, nextDiskCheck, lastModified, lastLength;
    public ProjectBuildSettingsDocument(SolutionProject project, EditorSessionHistory session)
    {
        this.project = project; history = new(this, session, "Edit project build settings");
        var bytes = File.ReadAllBytes(Path); properties = new(bytes, Path); savedDiskHash = SHA256.HashData(bytes);
        RememberFile(); SynchronizeProject();
    }
    public string Path => project.FullPath.ToOSPath();
    public bool IsDirty => revision != savedRevision;
    public long AuthoringRevision => revision;
    public void VerifySavedProject()
    {
        if (IsDirty) throw new InvalidOperationException("Save the project before restoring dependencies.");
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!SHA256.HashData(stream).AsSpan().SequenceEqual(savedDiskHash))
            throw new InvalidOperationException("The project file changed outside the editor.");
    }
    public bool CanSave => !project.Package.IsReadOnly;
    public bool CanUndo => history.Stack.CanRollback;
    public bool CanRedo => history.Stack.CanRollforward;
    public EditorSessionHistory? SessionHistory => history.SessionHistory;
    public void BindAuthoringState(EditorAuthoringState state) => history.Bind(state);
    public void CommitEdits() { }
    public void Undo() => SessionHistory!.Undo();
    public void Redo() => SessionHistory!.Redo();
    public void Dispose() { history.EnsureMayDispose(); properties.Dispose(); }
    public string? Raw(string name) => name == GraphicsProperty ? graphicsRaw
        : name == SolutionProject.ContainsAssetTypesProperty ? containsRaw : properties.Get(name);
    public ProjectGraphicsApi GraphicsApi => Enum.TryParse<ProjectGraphicsApi>(Raw(GraphicsProperty), true, out var value) && Enum.IsDefined(value) ? value : ProjectGraphicsApi.Default;
    public bool ContainsAssetTypes => bool.TryParse(Raw(SolutionProject.ContainsAssetTypesProperty), out var value) ? value : project.Type == ProjectType.Library;
    public void RefreshIfClean()
    {
        var now = Environment.TickCount64;
        if (IsDirty || now < nextDiskCheck) return;
        nextDiskCheck = now + 1000;
        var file = new FileInfo(Path);
        if (file.LastWriteTimeUtc.Ticks == lastModified && file.Length == lastLength) return;
        var bytes = File.ReadAllBytes(Path); properties.Load(bytes); savedDiskHash = SHA256.HashData(bytes);
        RememberFile(); SynchronizeProject();
    }
    public void SetGraphics(ProjectGraphicsApi value)
    {
        if (project.Type != ProjectType.Executable || project.Platform != Stride.Core.PlatformType.Windows || !Enum.IsDefined(value))
            throw new InvalidOperationException("This project doesn't support this graphics API setting.");
        Set(GraphicsProperty, value == ProjectGraphicsApi.Default ? null : value.ToString());
    }
    public void SetContains(bool? value) => Set(SolutionProject.ContainsAssetTypesProperty, value?.ToString().ToLowerInvariant());
    private void Set(string name, string? value)
    {
        if (!CanSave) throw new InvalidOperationException("This package can't be edited.");
        var before = properties.Snapshot; var beforeRevision = revision; var afterRevision = ++nextRevision;
        try
        {
            properties.Set(name, value); var after = properties.Snapshot;
            if (before == after) return;
            revision = afterRevision; SynchronizeProject();
            using var transaction = history.CreateTransaction(name == GraphicsProperty ? "Edit Graphics API" : "Edit Contains asset types");
            history.Record(new BuildOperation(this, before, after, beforeRevision, afterRevision));
        }
        catch { Apply(before, beforeRevision); throw; }
    }
    private void Apply(string snapshot, long nextRevisionValue) { properties.Restore(snapshot); revision = nextRevisionValue; SynchronizeProject(); }
    private void SynchronizeProject()
    {
        graphicsRaw = properties.Get(GraphicsProperty); containsRaw = properties.Get(SolutionProject.ContainsAssetTypesProperty);
        project.ContainsAssetTypes = bool.TryParse(containsRaw, out var value) ? value : null;
    }
    private void RememberFile() { var file = new FileInfo(Path); lastModified = file.LastWriteTimeUtc.Ticks; lastLength = file.Length; }
    public void Save()
    {
        history.EnsureMaySave();
        if (!CanSave) throw new InvalidOperationException("This package can't be edited.");
        if (!IsDirty) return;
        var bytes = SerializeWorkingProject();
        var result = WindowsOwnedAssetFileWriter.Write(Path, savedDiskHash, bytes);
        savedDiskHash = result.ContentHash; savedRevision = revision;
        lastModified = result.ModifiedTicks; lastLength = result.Length;
        history.Saved(savedDiskHash);
    }
    private byte[] SerializeWorkingProject()
    {
        // MSBuild's Save may normalize its mutable construction XML. Persisted
        // formatting must not silently replace an authored history fingerprint.
        var working = properties.Snapshot;
        try { return properties.Serialize(); }
        finally
        {
            if (properties.Snapshot != working) properties.Restore(working);
        }
    }
    private sealed class BuildOperation(ProjectBuildSettingsDocument owner, string before, string after, long beforeRevision, long afterRevision) : Operation
    {
        protected override void Rollback() => Replay(false);
        protected override void Rollforward() => Replay(true);
        private void Replay(bool forward)
        {
            if (owner.properties.Snapshot != (forward ? before : after)) throw new InvalidOperationException("The project build settings changed.");
            try
            {
                owner.Apply(forward ? after : before, forward ? afterRevision : beforeRevision);
            }
            catch { owner.Apply(forward ? before : after, forward ? beforeRevision : afterRevision); throw; }
        }
    }
}
