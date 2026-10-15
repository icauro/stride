using Stride.Core.Assets.Quantum;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Assets.Presentation.NodePresenters.Rules;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private readonly Dictionary<ItemId, EditorAssetPropertyPresentation> spriteFramePresentations = [];
    private readonly List<EditorAssetPropertyPresentation> retiringSpritePresentations = [];
    private long spriteFramePresentationVersion = -1;
    private Stride.Assets.Sprite.SpriteSheetType? spritePresentationSheetType;
    internal void PrepareSpriteFramePresentations()
    {
        EnsureSpriteSettingsGraph();
        if (HasActiveNativeMutationJournal) return;
        _ = ExtensionAssetPresentation;
        var extensions = SessionHistory?.Extensions;
        var sprites = (IAssetObjectNode)spriteSettingsGraph!.RootNode[nameof(Stride.Assets.Sprite.SpriteSheetAsset.Sprites)].Target!;
        var roots = SelectedSpriteIds.Where(sprites.HasId).ToDictionary(id => id, id => sprites.IndexedTarget(sprites.IdToIndex(id))!);
        var changed = spriteFramePresentations.Keys.Where(id => !roots.TryGetValue(id, out var node)
            || !ReferenceEquals(node, spriteFramePresentations[id].AuthoredRoot) || spriteFramePresentationVersion != extensions?.Version || spritePresentationSheetType != SpriteSettings!.Type).ToArray();
        if (changed.Length != 0) EndExtensionVirtualEdit();
        foreach (var id in changed) { retiringSpritePresentations.Add(spriteFramePresentations[id]); spriteFramePresentations.Remove(id); }
        DrainSpriteFramePresentations();
        spriteFramePresentationVersion = extensions?.Version ?? -1; spritePresentationSheetType = SpriteSettings!.Type;
        if (extensions == null) return;
        foreach (var root in roots)
            if (!spriteFramePresentations.ContainsKey(root.Key))
                spriteFramePresentations.Add(root.Key, new(AuthoringGraph, extensions, root.Value,
                    memberFilter: member => !ReferenceEquals(member.Parent, root.Value) || SpriteSheetNodeRules.IsFrameMemberVisible(SpriteSettings!.Type, member.Name)));
    }
    internal IReadOnlyList<INodePresenter> SpriteFramePresenterRoots
        => SelectedSpriteIds.Where(spriteFramePresentations.ContainsKey).Select(id => spriteFramePresentations[id].RootPresenter).ToArray();
    internal IReadOnlyList<INodePresenter> SpriteSheetPresenterRoots
        => SelectedSpriteIds.Count == 0 && ExtensionAssetPresentation is { } presentation ? [presentation.RootPresenter] : [];
    private EditorAssetPropertyPresentation? SpritePresentation(IMemberNode member)
        => spriteFramePresentations.Values.FirstOrDefault(presentation => presentation.Find(member) != null);
    private EditorAssetPropertyPresentation? SpritePresentation(IObjectNode collection)
        => spriteFramePresentations.Values.FirstOrDefault(presentation => EditorPresenterItemMetadata.FindCollection(presentation.RootPresenter, collection) != null);
    private EditorAssetPropertyPresentation? SpritePresentation(INodePresenter presenter)
        => spriteFramePresentations.Values.FirstOrDefault(presentation => presentation.OwnsPresenter(presenter))
            ?? (SelectedSpriteIds.Count == 0 && SpriteSettings != null && extensionAssetPresentation?.OwnsPresenter(presenter) == true ? extensionAssetPresentation : null);
    private IReadOnlyList<VirtualNodePresenter> SpriteFrameVirtualRows()
        => spriteFramePresentations.Values.SelectMany(presentation => presentation.VirtualRows).Distinct().ToArray();
    internal bool IsCurrentSpritePresenterGroup(IReadOnlyList<INodePresenter> nodes)
        => nodes.Count != 0 && (SelectedSpriteIds.Count == 0
            ? SpriteSettings != null && extensionAssetVersion == SessionHistory?.Extensions?.Version
                && nodes.All(node => extensionAssetPresentation?.OwnsPresenter(node) == true)
            : spriteFramePresentationVersion == SessionHistory?.Extensions?.Version
            && nodes.All(node => SpritePresentation(node) is { } presentation
                && spriteFramePresentations.Any(pair => ReferenceEquals(pair.Value, presentation) && selectedSpriteIds.Contains(pair.Key)
                    && spriteSettingsFrames.Any(frame => frame.Id == pair.Key && ReferenceEquals(frame.Fields.FirstOrDefault()?.Node.Parent, presentation.AuthoredRoot)))));
    internal bool IsCurrentSpriteVirtualGroup(IReadOnlyList<VirtualNodePresenter> nodes) => IsCurrentSpritePresenterGroup(nodes);
    internal object? ReadSpritePresenter(INodePresenter node) => node is VirtualNodePresenter virtualNode ? ReadExtensionVirtualProperty(virtualNode) : node.Value;
    internal bool CanEditSpritePresenter(INodePresenter node)
    {
        if (!IsCurrentSpritePresenterGroup([node])) return false;
        for (var current = node; current != null; current = current.Parent) if (current.IsReadOnly) return false;
        var accessor = node.GetNodeAccessor();
        return accessor.Node is IMemberNode member ? CanEditAssetSettingNode(member) && CanAssignAuthoringNode(member) && !IsExtensionPropertyReadOnly(member)
            : accessor.Node is IAssetObjectNode collection && CanEditAssetSettingItem(collection, accessor.Index);
    }
    internal EditorPresenterTarget AcquireSpritePresenterTarget(INodePresenter node)
        => IsCurrentSpritePresenterGroup([node]) && SpritePresentation(node) is { } presentation ? presentation.AcquireTarget(node)
            : throw new InvalidOperationException("The sprite presenter target changed.");
    internal PropertyNumericRange? SpritePresenterNumericRange(INodePresenter node) => SpritePresentation(node)?.NumericRange(node);
    internal object?[] CaptureSpritePresenterGroup(IReadOnlyList<INodePresenter> nodes)
    {
        if (!IsCurrentSpritePresenterGroup(nodes)) throw new InvalidOperationException("The sprite virtual property context changed.");
        return [.. nodes.Select(ReadSpritePresenter), .. nodes.Select(node => (object?)node),
            .. nodes.Select(node => (object?)node.GetNodeAccessor().Node), .. nodes.Select(node => (object?)node.GetNodeAccessor().Index),
            .. nodes.Select(node => ReadSpritePresenter(node) is DateTime value ? (object?)value.Kind : null),
            .. SpriteSettingsFrames.Where(frame => selectedSpriteIds.Contains(frame.Id)).SelectMany(frame => frame.Fields)
                .SelectMany(field => CaptureSpriteSettingDraft(field, false)),
            .. (SelectedSpriteIds.Count == 0 ? SpriteSettingsFields.SelectMany(field => CaptureSpriteSettingDraft(field, false)) : []),
            .. SelectedSpriteIds.Cast<object?>(), EditRevision, Path, SessionHistory?.Registry.Find(AuthoredAsset.Id)?.Generation,
            SessionHistory?.Extensions?.Version];
    }
    internal SpriteSettingField? SpritePresenterSourceField(INodePresenter presenter)
        => spriteSettingsFrames.Where(frame => selectedSpriteIds.Contains(frame.Id)).SelectMany(frame => frame.Fields)
            .FirstOrDefault(field => field.Type == typeof(Stride.Core.IO.UFile) && field.Node.Name == nameof(Stride.Assets.Sprite.SpriteInfo.Source)
                && ReferenceEquals(field.Node, presenter.GetNodeAccessor().Node));
    internal void TransformSpritePresenterGroup(IReadOnlyList<INodePresenter> nodes, Func<int, object?, object?> transform)
    {
        if (!IsCurrentSpritePresenterGroup(nodes) || nodes.Any(node => !CanEditSpritePresenter(node)))
            throw new InvalidOperationException("The selected sprite property changed.");
        if (nodes.Any(node => SpritePresenterSourceField(node) != null)) throw new InvalidOperationException("Sprite sources require decoded native assignment.");
        var values = nodes.Select((node, index) =>
        {
            var before = ReadSpritePresenter(node); var value = transform(index, before);
            var type = Nullable.GetUnderlyingType(node.Type) ?? node.Type;
            if (value == null ? node.Type.IsValueType && Nullable.GetUnderlyingType(node.Type) == null : !type.IsInstanceOfType(value))
                throw new ArgumentException("The sprite property value has an incompatible type.");
            NativeNumericAxes.AssertEditable(node, before, value);
            value = PropertyValueValidation.Normalize(value, SpritePresenterNumericRange(node));
            value = NativeNumericAxes.Normalize(value, before, SpritePresentation(node)?.NumericAxisRanges(node, []) ?? []);
            return (Node: node, Value: value);
        }).ToArray();
        ApplyInheritedAssetMutation(() => { foreach (var value in values) value.Node.UpdateValue(value.Value!); }, "Edit " + nodes[0].DisplayName);
    }
    internal IReadOnlyList<PropertyNumericRange?> SpritePresenterAxisRanges(INodePresenter node)
        => SpritePresentation(node)?.NumericAxisRanges(node, []) ?? [];
    private void DrainSpriteFramePresentations()
    {
        foreach (var presentation in retiringSpritePresentations.ToArray())
        { presentation.Dispose(); retiringSpritePresentations.Remove(presentation); }
    }
    private void DisposeSpriteFramePresentations()
    {
        retiringSpritePresentations.AddRange(spriteFramePresentations.Values); spriteFramePresentations.Clear();
        spriteFramePresentationVersion = -1; spritePresentationSheetType = null; DrainSpriteFramePresentations();
    }
}
