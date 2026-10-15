using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

/// <summary>A canonical authored graph generation, independently retained by views and actual undo operations.</summary>
internal abstract class EditorAuthoringState
{
    internal EditorAuthoringState(EditorAuthoringRegistry registry, IEditorDocument document, AssetId? assetId)
    { Registry = registry; Document = document; AssetId = assetId; CanonicalPath = Path.GetFullPath(document.Path); }
    internal EditorAuthoringRegistry Registry { get; }
    public Guid Generation { get; } = Guid.NewGuid();
    public string CanonicalPath { get; internal set; }
    public AssetId? AssetId { get; }
    public IEditorDocument Document { get; }
    public bool IsPublished { get; internal set; }
    public bool IsDisposed { get; internal set; }
    public int ViewReferences { get; internal set; }
    public int HistoryReferences { get; internal set; }
    public IDisposable AcquireView() => Registry.Acquire(this, false);
    internal IDisposable AcquireHistory() => Registry.Acquire(this, true);
}

internal sealed class EditorAuthoringState<TDocument> : EditorAuthoringState where TDocument : class, IEditorDocument
{
    internal EditorAuthoringState(EditorAuthoringRegistry registry, TDocument document, AssetId? assetId)
        : base(registry, document, assetId) { }
    public new TDocument Document => (TDocument)base.Document;
}
