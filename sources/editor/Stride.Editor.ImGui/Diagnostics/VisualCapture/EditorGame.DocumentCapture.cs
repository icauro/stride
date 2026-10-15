using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record DocumentCaptureContext(EditorDocumentEntry Entry, IEditorDocument Document,
        EditorAuthoringState State, VisualCaptureDocumentOwner Metadata)
    {
        internal string Root => "documents." + Entry.Id;
        internal string Qualify(string id) => Root + "." + id;
    }
    private DocumentCaptureContext? documentCaptureContext;
    private readonly struct DocumentCaptureScope(EditorGame? owner, DocumentCaptureContext? previous) : IDisposable
    { public void Dispose() { if (owner != null) owner.documentCaptureContext = previous; } }

    private DocumentCaptureScope BeginDocumentCapture(EditorDocumentEntry entry, EditorDocumentGroup group)
    {
        if (visualCapture == null) return default;
        var previous = documentCaptureContext;
        if (IsCurrentDocumentPresentation(entry))
        {
            var state = entry.State!;
            var context = new DocumentCaptureContext(entry, entry.Document, state,
                new(entry.Id.ToString(), entry.Path, entry.Kind, state.AssetId?.ToString(), group.Id.ToString(), state.Generation.ToString()));
            documentCaptureContext = context;
            var origin = Gui.GetCursorScreenPos();
            visualCapture.RegisterRegion(context.Root, "section", entry.Name, origin, origin + Gui.GetContentRegionAvail(),
                documentOwner: context.Metadata);
        }
        return new DocumentCaptureScope(this, previous);
    }

    private void RegisterOwnedCaptureRegion(string id, string kind, string name, Vector2 min, Vector2 max, string? parent, bool visible)
    {
        if (visualCapture == null) return;
        if (documentCaptureContext is not { } context)
        { visualCapture.RegisterRegion(id, kind, name, min, max, parent, visible); return; }
        if (!IsCurrentDocumentPresentation(context.Entry) || !ReferenceEquals(context.Entry.Document, context.Document)
            || !ReferenceEquals(context.Entry.State, context.State)) return;
        var scopedParent = parent == null ? context.Root : context.Qualify(parent);
        if (!visualCapture.HasTarget(scopedParent)) scopedParent = context.Root;
        visualCapture.RegisterRegion(context.Qualify(id), kind, name, min, max, scopedParent, visible, context.Metadata);
        if (ReferenceEquals(documents.Active, context.Entry))
            visualCapture.RegisterRegion(id, kind, name, min, max, scopedParent, visible, context.Metadata);
    }

    private bool TryResolveDocumentCapture(string id, out EditorDocumentEntry? entry, out string semantic)
    {
        entry = null; semantic = "";
        const string prefix = "documents.";
        if (!id.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var separator = id.IndexOf('.', prefix.Length);
        var identity = separator < 0 ? id[prefix.Length..] : id[prefix.Length..separator];
        if (!Guid.TryParse(identity, out var documentId)) return false;
        entry = documents.Entries.FirstOrDefault(candidate => candidate.Id == documentId && IsCurrentDocumentPresentation(candidate));
        semantic = separator < 0 ? "editor.document.content" : id[(separator + 1)..];
        return entry != null;
    }
}
