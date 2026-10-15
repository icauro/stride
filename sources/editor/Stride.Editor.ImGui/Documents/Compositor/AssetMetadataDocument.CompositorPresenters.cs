using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private readonly Dictionary<IObjectNode, (bool Entry, EditorAssetPropertyPresentation Presentation)> compositorPresentations = [];
    private readonly List<EditorAssetPropertyPresentation> retiringCompositorPresentations = [];
    internal long CompositorPresentationVersion { get; private set; }
    internal void SetCompositorPresentationRoots(IReadOnlyList<(IObjectNode Node, bool Entry)> roots)
    {
        _ = ExtensionAssetPresentation;
        foreach (var pair in compositorPresentations.ToArray())
        {
            if (roots.Any(root => ReferenceEquals(root.Node, pair.Key) && root.Entry == pair.Value.Entry)) continue;
            retiringCompositorPresentations.Add(pair.Value.Presentation); compositorPresentations.Remove(pair.Key);
            CompositorPresentationVersion++;
        }
        DrainCompositorPresentations();
        var extensions = SessionHistory?.Extensions;
        if (extensions == null) return;
        foreach (var root in roots)
        {
            if (compositorPresentations.ContainsKey(root.Node)) continue;
            var node = root.Node; var entries = root.Entry;
            var presentation = new EditorAssetPropertyPresentation(AuthoringGraph, extensions, node,
                shaderNames: () => MaterialShaderSources?.Invoke().Keys ?? Enumerable.Empty<string>(),
                memberFilter: member => !entries || !ReferenceEquals(member.Parent, node) || member.Name is "Game" or "Editor" or "SingleView");
            compositorPresentations.Add(node, (entries, presentation));
            CompositorPresentationVersion++;
            compositorPropertiesRevision = -1;
        }
    }
    private void DrainCompositorPresentations()
    {
        foreach (var presentation in retiringCompositorPresentations.ToArray())
        { presentation.Dispose(); retiringCompositorPresentations.Remove(presentation); }
    }
    private void DisposeCompositorPresentations()
    {
        retiringCompositorPresentations.AddRange(compositorPresentations.Values.Select(value => value.Presentation));
        if (compositorPresentations.Count != 0) CompositorPresentationVersion++;
        compositorPresentations.Clear(); compositorPropertiesRevision = -1;
        foreach (var property in compositorNativeProperties.Values) compositorPropertyMembers.Remove(property);
        compositorNativeProperties.Clear();
        DrainCompositorPresentations();
    }
    internal void ClearCompositorPresentationRoots() => DisposeCompositorPresentations();
    private EditorAssetPropertyPresentation? ResolveExtensionPresentation(IMemberNode member)
    {
        var fallback = ExtensionAssetPresentation;
        return SpritePresentation(member) ?? compositorPresentations.Values.Select(value => value.Presentation).FirstOrDefault(presentation => presentation.Find(member) != null) ?? fallback;
    }
    private EditorAssetPropertyPresentation? ResolveExtensionPresentation(IObjectNode collection)
    {
        var fallback = ExtensionAssetPresentation;
        return SpritePresentation(collection) ?? compositorPresentations.Values.Select(value => value.Presentation)
            .FirstOrDefault(presentation => EditorPresenterItemMetadata.FindCollection(presentation.RootPresenter, collection) != null) ?? fallback;
    }
    private EditorAssetPropertyPresentation? ResolveExtensionPresentation(INodePresenter presenter)
    {
        var fallback = ExtensionAssetPresentation;
        return SpritePresentation(presenter) ?? compositorPresentations.Values.Select(value => value.Presentation)
            .FirstOrDefault(presentation => presentation.OwnsPresenter(presenter)) ?? fallback;
    }
    private IReadOnlyList<VirtualNodePresenter> ActiveExtensionVirtualRows()
    {
        var fallback = ExtensionAssetPresentation;
        if (SpriteSettings != null && SelectedSpriteIds.Count != 0) return SpriteFrameVirtualRows();
        return CompositorAsset == null ? fallback?.VirtualRows ?? []
            : compositorPresentations.Values.SelectMany(value => value.Presentation.VirtualRows).Distinct().ToArray();
    }
}
