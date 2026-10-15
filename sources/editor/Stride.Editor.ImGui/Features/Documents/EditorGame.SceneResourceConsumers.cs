namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private (EngineViewport Viewport, EntityHierarchyDocument Document)[] SnapshotSceneResourceViews(bool includeRetiring = false)
    {
        var views = new Dictionary<EngineViewport, EntityHierarchyDocument>(ReferenceEqualityComparer.Instance);
        foreach (var view in sceneDocumentViews.Values)
            if (includeRetiring || IsCurrentSceneDocumentView(view)) views.TryAdd(view.Viewport, view.Document);
        if (includeRetiring)
            foreach (var view in retiredSceneDocumentBorrowers)
                views.TryAdd(view.Viewport, view.Document);
        if (engineViewport?.PresentedDocument is { } presented
            && (includeRetiring || engineViewport.RuntimeScene != null && engineViewport.SourceContent != null
                && !sceneDocumentViews.Values.Any(view => ReferenceEquals(view.Viewport, engineViewport))
                && documents.Entries.Any(entry => ReferenceEquals(entry.Document, presented) && IsCurrentDocumentPresentation(entry))))
            views.TryAdd(engineViewport, presented);
        return views.Select(pair => (pair.Key, pair.Value)).ToArray();
    }

    private bool SceneViewsUseImportedContent(IReadOnlyCollection<object> values)
        => SnapshotSceneResourceViews(includeRetiring: true)
            .Any(view => view.Viewport.UsesImportedContent(values, view.Document));
}
