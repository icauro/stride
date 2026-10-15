using Stride.Core.Quantum;
using Stride.Rendering.Compositing;

namespace Stride.Editor.ImGui;

internal sealed record CompositorOutputSlotCertificate(IObjectNode Root, Guid Owner, IObjectNode Source,
    IGraphNode Member, ISharedRenderer? Value, CompositorLinkPathElement[] Path)
{
    internal static CompositorOutputSlotCertificate? Capture(AssetMetadataDocument source, CompositorSlot slot)
    {
        var owner = source.CompositorBlocks.FirstOrDefault(block => block.Id == slot.Owner);
        var path = CompositorLinkSelectionCertificate.CapturePath(slot.Path);
        return owner == null || path == null ? null : new(source.AuthoringGraph.RootNode, slot.Owner, owner.Node,
            slot.Accessor.Node, slot.Renderer, path);
    }
    internal bool Matches(AssetMetadataDocument source, CompositorSlot slot)
        => slot.Owner == Owner && ReferenceEquals(Root, source.AuthoringGraph.RootNode)
            && source.CompositorBlocks.Any(block => block.Id == Owner && ReferenceEquals(block.Node, Source))
            && ReferenceEquals(slot.Accessor.Node, Member) && ReferenceEquals(slot.Renderer, Value)
            && CompositorLinkSelectionCertificate.MatchesPath(Path, slot.Path);
    internal CompositorSlot? Resolve(AssetMetadataDocument source)
    {
        var matches = source.CompositorBlocks.SelectMany(block => block.Slots).Where(slot => Matches(source, slot)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}

internal sealed partial class EditorGame
{
    private void BeginCompositorConnection(AssetMetadataDocument source, CompositorEditorState state, CompositorSlot slot)
        => Execute(() =>
    {
        var certificate = CompositorOutputSlotCertificate.Capture(source, slot);
        ExecuteCompositorCommand(source, () =>
        {
            if (!compositorEditors.TryGetValue(source, out var current) || !ReferenceEquals(current, state)
                || certificate?.Resolve(source) is not { } actual || !source.CanEditCompositorSlot(actual))
                throw new InvalidOperationException("The compositor connection source changed during validation.");
            state.Linking = actual; state.LinkingCertificate = certificate; state.PressedBlock = null;
        });
    });
    private static void ReconcileCompositorConnection(AssetMetadataDocument source, CompositorEditorState state)
    {
        if (state.LinkingCertificate?.Resolve(source) is { } actual && source.CanEditCompositorSlot(actual)) state.Linking = actual;
        else { state.Linking = null; state.LinkingCertificate = null; }
    }
    private Action? CaptureCompositorConnection(AssetMetadataDocument source, CompositorEditorState state, Guid targetId)
    {
        var certificate = state.LinkingCertificate;
        var target = source.CompositorBlocks.FirstOrDefault(block => block.Id == targetId && block.Value is ISharedRenderer);
        if (certificate == null || target == null || certificate.Resolve(source) is not { } slot || !slot.Accessor.AcceptValue(target.Value)) return null;
        return () =>
        {
            if (!compositorEditors.TryGetValue(source, out var current) || !ReferenceEquals(current, state)
                || certificate.Resolve(source) is not { } actual || !source.CanEditCompositorSlot(actual)
                || !source.CompositorBlocks.Any(block => block.Id == target.Id && ReferenceEquals(block.Value, target.Value)
                    && ReferenceEquals(block.Node, target.Node)) || !actual.Accessor.AcceptValue(target.Value))
                throw new InvalidOperationException("The compositor connection endpoints changed during validation.");
            source.ConnectCompositorSlot(actual, target.Id);
        };
    }
}
