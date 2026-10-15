using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly Dictionary<Guid, EditorDocumentEntry> visibleNonSceneDocuments = [];

    private bool IsCurrentDocumentPresentation(EditorDocumentEntry entry)
        => workspace != null && sessionHistory != null && documents.Entries.Contains(entry)
            && entry.ViewLease != null && entry.State is { IsPublished: true, IsDisposed: false } state
            && ReferenceEquals(state.Document, entry.Document)
            && ReferenceEquals(sessionHistory.Registry.Find(entry.Path), state);

    private bool CanPresentNonSceneDocument(EditorDocumentEntry entry)
    {
        if (!IsCurrentDocumentPresentation(entry)) return false;
        if (entry.Document is EntityHierarchyDocument) return false;
        return entry.Document is ProceduralAssetDocument or AssetMetadataDocument or ScriptSourceDocument or UIDocument;
    }

    private bool CanMutateDocumentPresentation(IEditorDocument source)
        => documents.Active is { } entry && ReferenceEquals(entry.Document, source)
            && IsCurrentDocumentPresentation(entry) && CanStartAssetEdit;

    private bool CanMoveNonSceneDocument(EditorDocumentGroupMove move)
        => ReferenceEquals(move.Entry.Document, move.Document) && ReferenceEquals(move.Entry.State, move.State)
            && !IsAuthoringPipelineUpdating && !IsAssetPreviewUpdating && projectLoading == null
            && CanPresentNonSceneDocument(move.Entry);

    private void SetNonSceneDocumentVisibility(EditorDocumentGroup group, EditorDocumentEntry? entry, bool visible)
    {
        if (visibleNonSceneDocuments.TryGetValue(group.Id, out var previous)
            && (!visible || !ReferenceEquals(previous, entry)))
        {
            HideNonSceneDocument(previous);
            visibleNonSceneDocuments.Remove(group.Id);
        }
        if (visible && entry != null && entry.Document is not EntityHierarchyDocument && IsCurrentDocumentPresentation(entry))
            visibleNonSceneDocuments[group.Id] = entry;
    }

    private void HideNonSceneDocument(EditorDocumentEntry entry)
    {
        if (entry.Document is UIDocument ui && ownedUIEditors.TryGetValue(ui, out var panel)) panel.HideDocumentPresentation();
        if (entry.Document is AssetMetadataDocument metadata && spriteCanvases.TryGetValue(metadata, out var sprite) && sprite.Preview != null)
            sprite.Preview.Visible = false;
        if (entry.Document is ScriptSourceDocument source && scriptEditors.TryGetValue(source, out var script)) script.SuspendSourcePresentation();
    }

    private void DrawNonSceneDocumentContents(EditorDocumentGroup group, EditorDocumentEntry entry, bool focused)
    {
        if (!CanPresentNonSceneDocument(entry)) return;
        var source = entry.Document; var state = entry.State; var expectedWorkspace = workspace; var history = sessionHistory;
        var commandOwner = ReferenceEquals(documents.Active, entry);
        if (focused && commandOwner)
        {
            assetEditorFocused = true;
            if (source is ProceduralAssetDocument or UIDocument) assetCommandContext = true;
        }
        Gui.PushID(entry.Id.ToString());
        try
        {
            // Focusing another group queues actual activation after all ImGui
            // scopes. This pass can show its real view, but cannot impersonate
            // the focused command owner to mutate canonical data.
            // Source views keep selection/copy/navigation available in a visible inactive
            // group. Every source mutation separately certifies the active canonical group.
            using var inactive = ImGuiScopes.Disabled(!commandOwner && source is not ScriptSourceDocument);
            switch (source)
            {
                case ProceduralAssetDocument procedural: DrawProceduralDocument(procedural); break;
                case VisualScriptDocument visual: DrawVisualScriptDocument(visual); break;
                case UIDocument ui: DrawUiDocument(ui); break;
                case AssetMetadataDocument metadata: DrawAssetMetadataDocument(metadata); break;
                case ScriptSourceDocument script: DrawScriptDocument(script); break;
            }
        }
        finally { Gui.PopID(); }
        if (!ReferenceEquals(workspace, expectedWorkspace) || !ReferenceEquals(sessionHistory, history)
            || !ReferenceEquals(entry.Document, source) || !ReferenceEquals(entry.State, state))
            throw new InvalidOperationException("The document presentation owner changed during rendering.");
    }

    private void ReleaseNonSceneDocumentPresentation(EditorDocumentEntry entry)
    {
        HideNonSceneDocument(entry);
        visualCapture?.ForgetDocument(entry.Id.ToString());
        foreach (var id in visibleNonSceneDocuments.Where(pair => ReferenceEquals(pair.Value, entry)).Select(pair => pair.Key).ToArray())
            visibleNonSceneDocuments.Remove(id);
    }

    private void ClearNonSceneDocumentPresentations()
    {
        foreach (var entry in visibleNonSceneDocuments.Values.Distinct()) HideNonSceneDocument(entry);
        visibleNonSceneDocuments.Clear();
        visualCapture?.ForgetDocumentTargets();
        documentCaptureContext = null;
    }
}
