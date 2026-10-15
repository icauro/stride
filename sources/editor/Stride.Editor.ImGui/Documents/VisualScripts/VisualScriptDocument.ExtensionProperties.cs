using Stride.Assets.Scripts;
using Stride.Core.Quantum;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys;
using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

internal sealed partial class VisualScriptDocument
{
    private Block? inspectedBlock;
    private readonly Dictionary<Block, EditorAssetPropertyPresentation> blockPresentations = new(ReferenceEqualityComparer.Instance);
    private readonly List<EditorAssetPropertyPresentation> retiredBlockPresentations = [];
    private long blockPresentationVersion = -1;
    private EditorAssetPropertyPresentation? BlockPresentation => inspectedBlock != null && blockPresentations.TryGetValue(inspectedBlock, out var presentation) ? presentation : null;

    internal void SelectInspectorBlocks(IReadOnlyCollection<Block> blocks)
    {
        if (blocks.Any(block => !Asset.Methods.Any(method => method.Blocks.Values.Contains(block))))
            throw new InvalidOperationException("The block has no current visual-script owner.");
        var extensions = SessionHistory?.Extensions;
        var versionChanged = blockPresentationVersion != extensions?.Version;
        var selected = blocks.ToHashSet(ReferenceEqualityComparer.Instance);
        bool Changed(Block block) => !selected.Contains(block) || blockPresentationVersion != extensions?.Version
            || !ReferenceEquals(blockPresentations[block].AuthoredRoot, ObjectNode(block));
        if (blockPresentations.Keys.Any(Changed)) { EndExtensionVirtualEdit(); EndGameSettingEdit(); }
        foreach (var block in blockPresentations.Keys.Where(Changed).ToArray())
        { retiredBlockPresentations.Add(blockPresentations[block]); blockPresentations.Remove(block); }
        foreach (var retired in retiredBlockPresentations.ToArray()) { retired.Dispose(); retiredBlockPresentations.Remove(retired); }
        blockPresentationVersion = extensions?.Version ?? -1;
        if (extensions != null)
            foreach (var block in blocks)
                if (!blockPresentations.ContainsKey(block)) blockPresentations.Add(block, new(AuthoringGraph, extensions, ObjectNode(block),
                    memberFilter: member => member.MemberDescriptor.MemberInfo.DeclaringType != typeof(Block)));
        if (inspectedBlock != null && !selected.Contains(inspectedBlock)) inspectedBlock = null;
        if (versionChanged) RefreshVisualScriptInspectorPresentation();
    }
    internal void SelectInspectorBlock(Block block)
    {
        if (!blockPresentations.ContainsKey(block) && SessionHistory?.Extensions != null)
            throw new InvalidOperationException("The block's selected native presentation changed.");
        inspectedBlock = block;
    }
    private EditorAssetPropertyPresentation? BlockPresentationFor(IMemberNode node)
        => blockPresentations.Values.FirstOrDefault(presentation => presentation.Find(node) != null);
    protected override INodePresenter? FindExtensionItemPresenter(IObjectNode collection, NodeIndex index)
        => blockPresentations.Values.Select(presentation => EditorPresenterItemMetadata.Find(presentation.RootPresenter, collection, index))
            .FirstOrDefault(presenter => presenter != null);
    internal override IEnumerable<AbstractNodeEntry>? ExtensionAbstractEntries(IMemberNode member)
        => BlockPresentationFor(member)?.AbstractEntries(member);
    internal override IEnumerable<AbstractNodeEntry>? ExtensionCollectionAbstractEntries(IObjectNode collection)
    {
        foreach (var presentation in blockPresentations.Values)
            if (EditorPresenterItemMetadata.FindCollection(presentation.RootPresenter, collection) is { } presenter
                && presenter.AttachedProperties.TryGetValue(AbstractNodeEntryData.Key, out var entries)) return entries;
        return null;
    }
    internal override EditorPresenterTarget? AcquireExtensionFactoryTarget(NodeAccessor accessor)
    {
        if (accessor.Node is IMemberNode member) return AcquireExtensionCommandTarget(member);
        if (accessor.Node is IObjectNode collection)
            foreach (var presentation in blockPresentations.Values)
            {
                var presenter = accessor.Index.IsEmpty ? EditorPresenterItemMetadata.FindCollection(presentation.RootPresenter, collection)
                    : EditorPresenterItemMetadata.Find(presentation.RootPresenter, collection, accessor.Index);
                if (presenter != null) return presentation.AcquireTarget(presenter);
            }
        return null;
    }
    internal override bool IsExtensionPropertyVisible(IMemberNode node) => BlockPresentationFor(node)?.IsVisible(node) ?? true;
    internal override bool IsExtensionPropertyReadOnly(IMemberNode node) => BlockPresentationFor(node)?.IsReadOnly(node) ?? false;
    internal override string? ExtensionPropertyLabel(IMemberNode node) => BlockPresentationFor(node)?.Label(node);
    internal override PropertyNumericRange? ExtensionNumericRange(IMemberNode node) => BlockPresentationFor(node)?.NumericRange(node);
    internal override PropertyNumericRange? ExtensionNumericRange(VirtualNodePresenter presenter)
        => blockPresentations.Values.FirstOrDefault(presentation => presentation.VirtualRows.Contains(presenter))?.NumericRange(presenter);
    internal override EditorPresenterTarget? AcquireExtensionCommandTarget(IMemberNode node) => BlockPresentationFor(node)?.AcquireCommandTarget(node);
    internal override IReadOnlyList<VirtualNodePresenter> ExtensionVirtualProperties => BlockPresentation?.VirtualRows ?? [];
    internal override EditorPresenterTarget AcquireExtensionVirtualTarget(VirtualNodePresenter presenter)
        => blockPresentations.Values.FirstOrDefault(presentation => presentation.VirtualRows.Contains(presenter))?.AcquireVirtualTarget(presenter)
            ?? throw new InvalidOperationException("The block's native presenter is no longer active.");
    internal override bool CanEditExtensionVirtualProperty(VirtualNodePresenter presenter)
        => blockPresentations.Values.Any(presentation => presentation.VirtualRows.Contains(presenter)) && base.CanEditExtensionVirtualProperty(presenter);
    internal override void DisposeExtensionAssetPresentation()
    {
        retiredBlockPresentations.AddRange(blockPresentations.Values); blockPresentations.Clear();
        inspectedBlock = null; blockPresentationVersion = -1;
        foreach (var retired in retiredBlockPresentations.ToArray()) { retired.Dispose(); retiredBlockPresentations.Remove(retired); }
        base.DisposeExtensionAssetPresentation();
    }
}
