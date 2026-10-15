using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Rendering.Compositing;

namespace Stride.Editor.ImGui;

internal sealed record CompositorLinkPathElement(GraphNodePath.ElementType Type, string? Name, IGraphNode Parent, ItemId? Item, NodeIndex Index);
internal sealed record CompositorLinkSelectionCertificate(IObjectNode Root, Guid Owner, IObjectNode Source,
    IGraphNode Member, ItemId? Item, NodeIndex Index, ISharedRenderer Renderer, IObjectNode Target,
    CompositorLinkPathElement[] Path)
{
    internal static CompositorLinkPathElement[]? CapturePath(GraphNodePath path)
    {
        var prefix = new GraphNodePath(path.RootNode);
        var elements = new List<CompositorLinkPathElement>();
        foreach (var element in path.Path)
        {
            var parent = prefix.GetNode(); ItemId? item = null;
            if (element.Type == GraphNodePath.ElementType.Index)
            {
                if (parent is not IAssetObjectNode collection || !collection.TryIndexToId(element.Index, out var actual)) return null;
                item = actual;
            }
            elements.Add(new(element.Type, element.Name, parent, item, element.Index));
            switch (element.Type)
            {
                case GraphNodePath.ElementType.Member: prefix.PushMember(element.Name!); break;
                case GraphNodePath.ElementType.Target: prefix.PushTarget(); break;
                case GraphNodePath.ElementType.Index: prefix.PushIndex(element.Index); break;
            }
        }
        return elements.ToArray();
    }
    internal static bool MatchesPath(CompositorLinkPathElement[] expectedPath, GraphNodePath actualPath)
    {
        var path = CapturePath(actualPath);
        if (path == null || path.Length != expectedPath.Length) return false;
        for (var index = 0; index < path.Length; index++)
        {
            var expected = expectedPath[index]; var actual = path[index];
            if (expected.Type != actual.Type || expected.Name != actual.Name || !ReferenceEquals(expected.Parent, actual.Parent)
                || expected.Item != actual.Item || expected.Item == null && expected.Index != actual.Index) return false;
        }
        return true;
    }
    internal static CompositorLinkSelectionCertificate? Capture(AssetMetadataDocument document, CompositorSlot slot)
    {
        var blocks = document.CompositorBlocks;
        var source = blocks.FirstOrDefault(block => block.Id == slot.Owner);
        var renderer = slot.Renderer;
        var target = blocks.FirstOrDefault(block => ReferenceEquals(block.Value, renderer));
        if (source == null || renderer == null || target == null) return null;
        var accessor = slot.Accessor;
        ItemId? item = null;
        if (accessor.IsItem)
        {
            if (accessor.Node is not IAssetObjectNode collection || !collection.TryIndexToId(accessor.Index, out var actualItem)) return null;
            item = actualItem;
        }
        var path = CapturePath(slot.Path); if (path == null) return null;
        return new(document.AuthoringGraph.RootNode, slot.Owner, source.Node, accessor.Node, item, accessor.Index, renderer, target.Node, path);
    }
    internal bool Matches(AssetMetadataDocument document, CompositorSlot slot)
    {
        if (!ReferenceEquals(Root, document.AuthoringGraph.RootNode) || slot.Owner != Owner || !ReferenceEquals(slot.Renderer, Renderer)) return false;
        var blocks = document.CompositorBlocks;
        if (!blocks.Any(block => block.Id == Owner && ReferenceEquals(block.Node, Source))
            || !blocks.Any(block => ReferenceEquals(block.Value, Renderer) && ReferenceEquals(block.Node, Target))) return false;
        var accessor = slot.Accessor;
        if (!ReferenceEquals(accessor.Node, Member)) return false;
        if (!MatchesPath(Path, slot.Path)) return false;
        return Item is { } item ? accessor.Node is IAssetObjectNode collection && collection.TryIndexToId(accessor.Index, out var actual) && actual == item
            : accessor.Index == Index;
    }
}

internal sealed partial class EditorGame
{
    private static void ReconcileCompositorLinkSelection(AssetMetadataDocument source, CompositorEditorState state)
    {
        if (state.SelectedLinks.Count == 0) { state.SelectedLinkCertificates.Clear(); return; }
        var slots = source.CompositorBlocks.SelectMany(block => block.Slots).ToArray();
        var retained = new Dictionary<CompositorSlot, CompositorLinkSelectionCertificate>();
        foreach (var selected in state.SelectedLinks)
        {
            if (!state.SelectedLinkCertificates.TryGetValue(selected, out var certificate)) continue;
            var matches = slots.Where(slot => certificate.Matches(source, slot)).ToArray();
            // Multiple aliased paths cannot identify one original rendered edge safely.
            if (matches.Length == 1) retained.TryAdd(matches[0], certificate);
        }
        state.SelectedLinks.Clear(); state.SelectedLinkCertificates.Clear();
        foreach (var pair in retained) { state.SelectedLinks.Add(pair.Key); state.SelectedLinkCertificates.Add(pair.Key, pair.Value); }
    }
}
