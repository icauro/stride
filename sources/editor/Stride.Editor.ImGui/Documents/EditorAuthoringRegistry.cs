using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

/// <summary>Session graph ownership. Unpublished generations remain alive while real history refers to them.</summary>
internal sealed class EditorAuthoringRegistry : IDisposable
{
    private readonly Dictionary<string, EditorAuthoringState> paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<AssetId, EditorAuthoringState> assets = [];
    private readonly HashSet<EditorAuthoringState> states = [];
    private readonly HashSet<EditorAuthoringState> unused = [];
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private bool disposed;
    private readonly Func<string, bool>? isReadOnlyPath;
    public EditorAuthoringRegistry(Func<string, bool>? isReadOnlyPath = null) => this.isReadOnlyPath = isReadOnlyPath;
    public IEnumerable<EditorAuthoringState> PublishedStates => paths.Values;
    public EditorAuthoringState? Find(string path) => paths.GetValueOrDefault(Path.GetFullPath(path));
    public EditorAuthoringState? Find(AssetId id) => assets.GetValueOrDefault(id);
    internal bool Owns(IEditorDocument document) => states.Any(state => ReferenceEquals(state.Document, document));
    internal IReadOnlyList<EditorAuthoringState> RetainedStates
    {
        get { CheckThread(); return states.Where(state => !state.IsDisposed).ToArray(); }
    }
    internal IReadOnlyList<EditorAuthoringState> GetRetainedStates(AssetId id)
    {
        CheckThread();
        return states.Where(state => !state.IsDisposed && state.AssetId == id).ToArray();
    }
    private void CopyDocumentFileProvenance(IEditorDocument document, AssetId? id)
    {
        if (document is not AssetMetadataDocument target || target.GameSettings == null || id == null) return;
        var source = states.Where(state => !state.IsDisposed && state.AssetId == id && !ReferenceEquals(state.Document, document)
            && state.Document is AssetMetadataDocument { GameSettings: not null }).OrderByDescending(state => state.IsPublished)
            .ThenByDescending(state =>
            {
                ((AssetMetadataDocument)state.Document).TryGetSavedGameOrientationChange(out _, out var version);
                return version;
            }).FirstOrDefault()?.Document as AssetMetadataDocument;
        if (source != null) target.CopyGameSettingsOrientationProvenanceFrom(source);
    }
    internal void RefreshDocumentFileProvenance(IEditorDocument document, AssetId? id)
    {
        CheckThread(); CopyDocumentFileProvenance(document, id);
    }
    internal void NotifyOwnedFileSaved(EditorAuthoringState saving, byte[] contentHash)
    {
        CheckState(saving); ArgumentNullException.ThrowIfNull(contentHash);
        if (!saving.IsPublished || !ReferenceEquals(Find(saving.Document.Path), saving))
            throw new InvalidOperationException("Only the canonical published generation can publish a saved file baseline.");
        foreach (var state in states.Where(state => !state.IsDisposed && (saving.AssetId != null
            ? state.AssetId == saving.AssetId
            : state.AssetId == null && state.Document.GetType() == saving.Document.GetType()
                && string.Equals(state.CanonicalPath, saving.CanonicalPath, StringComparison.OrdinalIgnoreCase))))
            if (state.Document is IEditorOwnedFileGeneration owner) owner.AcceptOwnedSaveBaseline(contentHash.ToArray(), ReferenceEquals(state, saving));
    }

    public EditorAuthoringState<TDocument> GetOrCreate<TDocument>(string path, AssetId? id, Func<TDocument> factory)
        where TDocument : class, IEditorDocument
    {
        CheckThread();
        var canonical = Path.GetFullPath(path);
        if (paths.TryGetValue(canonical, out var existing))
            return existing is EditorAuthoringState<TDocument> typed && existing.AssetId == id ? typed
                : throw new InvalidOperationException("The asset already has a different authored document.");
        var document = factory();
        try
        {
            if (!string.Equals(Path.GetFullPath(document.Path), canonical, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The authored document does not match its requested path.");
            return Register(document, id);
        }
        catch { if (!Owns(document)) document.Dispose(); throw; }
    }
    public EditorAuthoringState<TDocument> Register<TDocument>(TDocument document, AssetId? id = null)
        where TDocument : class, IEditorDocument
        => RegisterCore(document, id, true);
    public EditorAuthoringState<TDocument> RegisterUnpublished<TDocument>(TDocument document, AssetId? id = null)
        where TDocument : class, IEditorDocument
        => RegisterCore(document, id, false);
    private EditorAuthoringState<TDocument> RegisterCore<TDocument>(TDocument document, AssetId? id, bool publish)
        where TDocument : class, IEditorDocument
    {
        CheckThread();
        ArgumentNullException.ThrowIfNull(document);
        if (id is { } identity && document is IAssetTagDocument tagged && tagged.AuthoredAsset.Id != identity)
            throw new InvalidOperationException("The authored asset does not match its registered identity.");
        var state = new EditorAuthoringState<TDocument>(this, document, id);
        if (isReadOnlyPath?.Invoke(state.CanonicalPath) == true && document.CanSave)
            throw new InvalidOperationException("A read-only package requires a read-only authored document.");
        if (publish) ValidatePublication(state, state.CanonicalPath);
        CopyDocumentFileProvenance(document, id);
        states.Add(state);
        EditorOperationCapture? initialization = null;
        try
        {
            if (document is ISessionAuthoringDocument authored && authored.SessionHistory is { } history)
            {
                if (!history.IsCapturingOperations)
                {
                    initialization = history.CaptureOperationsAtomic(() =>
                    {
                        authored.BindAuthoringState(state);
                        if (publish) Publish(state);
                    });
                    // Prepared generations are initialized as part of their real
                    // eventual graph-swap/creation operation, not a ghost undo entry.
                    if (publish && initialization.Entries.Count != 0) initialization.Publish(history, "Initialize asset");
                }
                else { authored.BindAuthoringState(state); if (publish) Publish(state); }
            }
            else if (publish) Publish(state);
            return state;
        }
        catch
        {
            // A completion observer can fail after the real operation is published.
            // Preserve its actual canonical owner rather than disposing replayable data.
            if (initialization?.IsPublished == true) throw;
            RemoveIndexes(state); state.IsPublished = false;
            if (state.HistoryReferences != 0 || state.ViewReferences != 0) ReleaseIfUnused(state);
            else { state.IsDisposed = true; states.Remove(state); }
            throw;
        }
        finally { initialization?.Dispose(); }
    }
    public void Publish(EditorAuthoringState state)
    {
        CheckState(state);
        CopyDocumentFileProvenance(state.Document, state.AssetId);
        var canonical = Path.GetFullPath(state.Document.Path);
        ValidatePublication(state, canonical);
        if (state.IsPublished) RemoveIndexes(state);
        state.CanonicalPath = canonical; state.IsPublished = true;
        paths[canonical] = state;
        if (state.AssetId is { } id) assets[id] = state;
        if (state.Document is AssetMetadataDocument metadata) metadata.ReconcilePublishedGameOrientationProvenance();
    }
    public void Unpublish(EditorAuthoringState state)
    {
        CheckState(state); RemoveIndexes(state); state.IsPublished = false;
    }
    public void Reindex(EditorAuthoringState state) => Publish(state);
    internal IDisposable Acquire(EditorAuthoringState state, bool history)
    {
        CheckState(state);
        if (history) state.HistoryReferences++; else state.ViewReferences++;
        unused.Remove(state); return new Lease(this, state, history);
    }
    public void ReleaseIfUnused(EditorAuthoringState state)
    {
        CheckState(state);
        if (state.ViewReferences == 0 && state.HistoryReferences == 0) unused.Add(state);
    }
    // Disposal is collected outside core stack callbacks, so a graph disposer
    // cannot interrupt transaction purging or its lifetime bookkeeping.
    public void CollectUnused()
    {
        CheckThread();
        List<Exception>? errors = null;
        foreach (var state in unused.ToArray())
        {
            if (state.ViewReferences != 0 || state.HistoryReferences != 0) { unused.Remove(state); continue; }
            if (state.IsPublished && state.Document.IsDirty) continue;
            RemoveIndexes(state); state.IsPublished = false; state.IsDisposed = true;
            try { state.Document.Dispose(); states.Remove(state); unused.Remove(state); }
            catch (Exception error) { state.IsDisposed = false; (errors ??= []).Add(error); }
        }
        if (errors != null) throw new AggregateException("Authored graph cleanup is incomplete.", errors);
    }
    private void ValidatePublication(EditorAuthoringState state, string path)
    {
        if (paths.TryGetValue(path, out var occupied) && !ReferenceEquals(occupied, state)
            || state.AssetId is { } id && assets.TryGetValue(id, out occupied) && !ReferenceEquals(occupied, state))
            throw new InvalidOperationException("This asset already has a published authored state.");
    }
    private void RemoveIndexes(EditorAuthoringState state)
    {
        if (ReferenceEquals(paths.GetValueOrDefault(state.CanonicalPath), state)) paths.Remove(state.CanonicalPath);
        if (state.AssetId is { } id && ReferenceEquals(assets.GetValueOrDefault(id), state)) assets.Remove(id);
    }
    private void CheckThread()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Environment.CurrentManagedThreadId != ownerThread) throw new InvalidOperationException("Authored state must be accessed on its owning editor thread.");
    }
    internal void EnsureOwnerThread() => CheckThread();
    private void CheckState(EditorAuthoringState state)
    {
        CheckThread();
        if (!ReferenceEquals(state.Registry, this) || state.IsDisposed || !states.Contains(state))
            throw new InvalidOperationException("The authored state does not belong to this session.");
    }
    public void Dispose()
    {
        if (disposed) return;
        CheckThread();
        if (states.Any(state => state.HistoryReferences != 0 || state.ViewReferences != 0))
            throw new InvalidOperationException("Release history and view leases before disposing authored states.");
        List<Exception>? errors = null;
        foreach (var state in states.ToArray())
        {
            RemoveIndexes(state);
            state.IsDisposed = true; state.IsPublished = false;
            try { state.Document.Dispose(); states.Remove(state); unused.Remove(state); }
            catch (Exception error) { state.IsDisposed = false; unused.Add(state); (errors ??= []).Add(error); }
        }
        paths.Clear(); assets.Clear();
        if (errors != null) throw new AggregateException(errors);
        disposed = true; unused.Clear();
    }
    private sealed class Lease(EditorAuthoringRegistry registry, EditorAuthoringState state, bool history) : IDisposable
    {
        private bool released;
        public void Dispose()
        {
            if (released) return;
            registry.CheckState(state);
            if (history) state.HistoryReferences--; else state.ViewReferences--;
            released = true; registry.ReleaseIfUnused(state);
        }
    }
}
