namespace Stride.Editor.ImGui;

internal sealed class EditorDocumentEntry(IEditorDocument document)
{
    public Guid Id { get; } = Guid.NewGuid();
    public IEditorDocument Document { get; internal set; } = document;
    internal EditorAuthoringState? State;
    internal IDisposable? ViewLease;
    public string Path => Document.Path;
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string Kind => Document switch
    {
        PrefabDocument => "Prefab",
        EntityHierarchyDocument => "Scene",
        ProceduralAssetDocument => "Procedural model",
        VisualScriptDocument => "Visual script",
        AssetMetadataDocument { TextureSettings: not null } => "Texture",
        AssetMetadataDocument { AuthoredAsset: Stride.Assets.Materials.MaterialAsset } => "Material",
        AssetMetadataDocument { GameSettings: not null } => "Game settings",
        AssetMetadataDocument { SpriteSettings: not null } => "Sprite sheet",
        AssetMetadataDocument { SkyboxSettings: not null } => "Skybox",
        AssetMetadataDocument { IsFontAsset: true } => "Font",
        UIDocument { Asset: Stride.UI.Assets.UILibraryAsset } => "UI library",
        UIDocument => "UI page",
        AssetMetadataDocument => "Tags",
        ScriptSourceDocument => "Script",
        _ => Document.GetType().Name,
    };
}

/// <summary>Owns retained authored documents. Renderer/view state stays with the host.</summary>
internal sealed partial class EditorDocumentSession : IDisposable
{
    private readonly List<EditorDocumentEntry> entries = [];
    private readonly Dictionary<string, EditorDocumentEntry> paths = new(StringComparer.OrdinalIgnoreCase);
    private EditorAuthoringRegistry? registry;
    public IReadOnlyList<EditorDocumentEntry> Entries => entries;
    public IEnumerable<IEditorDocument> AuthoredDocuments => registry?.PublishedStates.Select(state => state.Document)
        ?? entries.Select(entry => entry.Document);
    public EditorDocumentEntry? Active { get; private set; }
    public bool HasDirtyDocuments => registry?.PublishedStates.Any(state => state.Document is not PackageMetadataDocument && state.Document.IsDirty)
        ?? entries.Any(entry => entry.Document.IsDirty);
    public void Configure(EditorAuthoringRegistry? authoring)
    {
        if (entries.Count != 0) throw new InvalidOperationException("Close existing views before changing authoring sessions.");
        registry = authoring;
    }
    public EditorDocumentEntry? Find(string path) => paths.GetValueOrDefault(System.IO.Path.GetFullPath(path));
    public EditorDocumentEntry? Find(IEditorDocument? document) => entries.FirstOrDefault(entry => ReferenceEquals(entry.Document, document));
    public EditorDocumentEntry Add(IEditorDocument document)
    {
        var path = System.IO.Path.GetFullPath(document.Path);
        if (paths.ContainsKey(path)) throw new InvalidOperationException("This document is already open.");
        var entry = new EditorDocumentEntry(document);
        AttachLease(entry);
        entries.Add(entry); paths.Add(path, entry); return entry;
    }
    public void Activate(EditorDocumentEntry entry)
    {
        if (!entries.Contains(entry)) throw new InvalidOperationException("This document is no longer open.");
        Active?.Document.CommitEdits(); Active = entry;
    }
    public void RelocatePath(EditorDocumentEntry entry, string nextPath)
    {
        var previousPath = System.IO.Path.GetFullPath(entry.Path);
        nextPath = System.IO.Path.GetFullPath(nextPath);
        if (string.Equals(previousPath, nextPath, StringComparison.Ordinal)) return;
        if (!ReferenceEquals(paths.GetValueOrDefault(previousPath), entry))
            throw new InvalidOperationException("The document is no longer open at its original path.");
        if (paths.TryGetValue(nextPath, out var conflict) && !ReferenceEquals(conflict, entry))
            throw new InvalidOperationException("A document is already open at the destination path.");
        if (entry.Document is not IRelocatableEditorDocument relocatable)
            throw new InvalidOperationException("This document cannot be moved.");
        if (string.Equals(previousPath, nextPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A document cannot be moved to the same path.");
        paths.Add(nextPath, entry);
        try { relocatable.RelocatePath(nextPath); if (entry.State != null) registry!.Reindex(entry.State); }
        catch { paths.Remove(nextPath); relocatable.RelocatePath(previousPath); if (entry.State != null) registry!.Reindex(entry.State); throw; }
        paths.Remove(previousPath);
    }
    public EditorDocumentEntry? Remove(EditorDocumentEntry entry)
    {
        var index = entries.IndexOf(entry);
        if (index < 0) return Active;
        entries.RemoveAt(index); paths.Remove(System.IO.Path.GetFullPath(entry.Path));
        if (ReferenceEquals(Active, entry)) Active = entries.Count == 0 ? null : entries[Math.Min(index, entries.Count - 1)];
        ReleaseEntry(entry); return Active;
    }
    public void ApplyOrder(IReadOnlyList<(EditorDocumentEntry Entry, int Order)> order)
    {
        if (order.Count != entries.Count) return;
        for (var index = 0; index < entries.Count; index++)
            if (!ReferenceEquals(entries[index], order[index].Entry))
            {
                var reordered = order.Select(item => item.Entry).ToArray();
                if (reordered.Distinct().Count() != entries.Count || reordered.Any(entry => !entries.Contains(entry)))
                    throw new InvalidOperationException("Tab order must contain each open document exactly once.");
                entries.Clear(); entries.AddRange(reordered); return;
            }
    }
    public void CommitAll()
    {
        if (registry != null) foreach (var state in registry.PublishedStates.ToArray()) state.Document.CommitEdits();
        else foreach (var entry in entries) entry.Document.CommitEdits();
    }
    private static int SaveOrder(IEditorDocument document) => document switch
    {
        VisualScriptDocument => 0,
        ScriptSourceDocument { GeneratedByAssetId: not null } => 2,
        ProjectBuildSettingsDocument => 3,
        _ => 1,
    };
    public void SaveAll() => SaveAll(null);
    public void SaveAll(Action? validateContext)
    {
        CommitAll();
        validateContext?.Invoke();
        if (registry != null)
        {
            var pending = registry.PublishedStates
                .Where(state => state.Document is not PackageMetadataDocument && state.Document.IsDirty)
                .OrderBy(state => SaveOrder(state.Document)).ToArray();
            foreach (var state in pending)
            {
                validateContext?.Invoke();
                if (!state.IsPublished || state.IsDisposed || !ReferenceEquals(registry.Find(state.CanonicalPath), state))
                    throw new InvalidOperationException("The document save owner changed.");
                state.Document.Save();
            }
        }
        else foreach (var entry in entries.Where(entry => entry.Document.IsDirty)
            .OrderBy(entry => SaveOrder(entry.Document)).ToArray())
        {
            validateContext?.Invoke();
            if (!entries.Contains(entry)) throw new InvalidOperationException("The document save view changed.");
            entry.Document.Save();
        }
    }
    public void Dispose()
    {
        foreach (var entry in entries) ReleaseEntry(entry);
        entries.Clear(); paths.Clear(); Active = null;
    }
    private void AttachLease(EditorDocumentEntry entry)
    {
        if (registry == null) return;
        var state = registry.Find(entry.Path);
        if (state == null) state = registry.Register(entry.Document, entry.Document is IAssetTagDocument tagged ? tagged.AuthoredAsset.Id : null);
        if (!ReferenceEquals(state.Document, entry.Document)) throw new InvalidOperationException("Bind the existing canonical authored document.");
        entry.State = state; entry.ViewLease = state.AcquireView();
    }
    private void ReleaseEntry(EditorDocumentEntry entry)
    {
        if (entry.ViewLease != null) { entry.ViewLease.Dispose(); entry.ViewLease = null; }
        else if (registry == null) entry.Document.Dispose();
    }
    public void ReplaceViewDocument(EditorDocumentEntry entry, EditorAuthoringState state)
    {
        if (!entries.Contains(entry)) return;
        var previous = entry.Document; var previousLease = entry.ViewLease;
        var lease = state.AcquireView();
        entry.Document = state.Document; entry.State = state; entry.ViewLease = lease;
        paths.Remove(System.IO.Path.GetFullPath(previous.Path)); paths[System.IO.Path.GetFullPath(state.Document.Path)] = entry;
        previousLease?.Dispose();
    }
}
