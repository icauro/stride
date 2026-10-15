using Stride.Assets.Sprite;
using Stride.Core.Assets;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel.CopyPasteProcessors;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets.Serializers;
using Stride.Core.Yaml;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private CopyPasteService? spriteClipboard;
    private CopyPasteService SpriteClipboard
    {
        get
        {
            EnsureSpriteSettingsGraph();
            if (spriteClipboard != null) return spriteClipboard;
            var settings = AssetYamlSerializer.Default.GetSerializerSettings();
            settings.RegisterTagMapping("!CopyPasteData", typeof(CopyPasteData)); settings.RegisterTagMapping("!CopyPasteItem", typeof(CopyPasteItem));
            spriteClipboard = new CopyPasteService(spriteSettingsGraph!.Container);
            spriteClipboard.RegisterProcessor(new AssetPropertyPasteProcessor());
            return spriteClipboard;
        }
    }
    public string CopySelectedSpriteFrames()
        => CopySpriteFrames(SelectedSpriteIds);
    private string CopySpriteFrames(IReadOnlyCollection<Stride.Core.Reflection.ItemId> ids)
    {
        CommitEdits(); EnsureSpriteSettingsGraph();
        var values = ids.Select(id => spriteSettingsFrames.FirstOrDefault(frame => frame.Id == id)).Where(frame => frame != null).Select(frame => frame!.Value).ToList();
        if (values.Count == 0) return "";
        // WPF's session source paths are resolved before copy. A directly loaded canonical
        // document can still contain relative YAML paths; resolve only the copied snapshot.
        // Only this temporary selection container is initialized before reading native
        // overrides. Its elements still belong to the canonical graph and must not be
        // traversed by the mutating ID generator during a Copy command.
        var selectionIds = CollectionItemIdHelper.GetCollectionItemIds(values);
        for (var index = 0; index < values.Count; index++) selectionIds.Add(index, ItemId.New());
        var nodes = spriteSettingsGraph!.Container.NodeContainer;
        var overrides = AssetPropertyGraph.GenerateOverridesForSerialization(nodes.GetOrCreateNode(values));
        var copied = AssetCloner.Clone(values);
        AssetCollectionItemIdHelper.GenerateMissingItemIds(copied);
        foreach (var sprite in copied)
            if (!string.IsNullOrEmpty(sprite.Source) && !sprite.Source.IsAbsolute)
                sprite.Source = new Stride.Core.IO.UFile(System.IO.Path.GetFullPath(sprite.Source, System.IO.Path.GetDirectoryName(Path)!));
        AssetPropertyGraph.ApplyOverrides((IAssetNode)nodes.GetOrCreateNode(copied), overrides);
        return SpriteClipboard.CopyFromAsset(spriteSettingsGraph, AuthoredAsset.Id, copied, false);
    }
    public bool CanTransferSpriteFrames(AssetMetadataDocument source) => CanEditSpriteCollection && source.CanEditSpriteCollection
        && SessionHistory != null && ReferenceEquals(SessionHistory, source.SessionHistory);
    public void TransferSpriteFrames(AssetMetadataDocument source, IReadOnlyCollection<Stride.Core.Reflection.ItemId> ids,
        Stride.Core.Reflection.ItemId? target, bool after)
    {
        if (ReferenceEquals(source, this)) { InsertSpriteFramesBefore(ids, target, after); return; }
        if (!CanTransferSpriteFrames(source)) throw new InvalidOperationException("These sprite sheets must belong to the same authored session.");
        var sourceSprites = source.EditableSprites(); var destinationSprites = EditableSprites();
        if (target is { } targetId && !destinationSprites.HasId(targetId)) throw new InvalidOperationException("The destination sprite no longer exists.");
        var originals = source.spriteSettingsFrames.Where(frame => ids.Contains(frame.Id)).ToArray();
        foreach (var frame in originals)
            if (!source.CanEditAssetSettingItem(sourceSprites, sourceSprites.IdToIndex(frame.Id))) throw new InvalidOperationException("This sprite cannot be edited.");
        var copied = source.CopySpriteFrames(ids);
        if (!CanPasteSpriteFrames(copied)) throw new InvalidOperationException("The sprites cannot be transferred to this sheet.");
        var result = SpriteClipboard.DeserializeCopiedData(copied, AuthoredAsset, typeof(List<SpriteInfo>));
        if (result.Items.FirstOrDefault() is not { Processor: AssetPropertyPasteProcessor, Data: List<SpriteInfo> values } || values.Count != originals.Length)
            throw new InvalidOperationException("The sprite transfer could not be resolved.");
        var detector = new UnloadableDetector(); detector.Visit(values);
        if (detector.Found) throw new InvalidOperationException("The copied sprites contain unresolved types.");
        var insertion = target is { } beforeId ? destinationSprites.IdToIndex(beforeId).Int + (after ? 1 : 0) : SpriteSettings!.Sprites.Count;
        // Each graph records its own canonical participant and lease into the same
        // session transaction, so closing either tab retains both undo owners.
        void Apply()
        {
            using var transaction = CreateDocumentTransaction("Move images");
            source.ApplyInheritedAssetMutation(() =>
            {
                foreach (var frame in originals.Reverse())
                    sourceSprites.Remove(frame.Value, sourceSprites.IdToIndex(frame.Id));
            }, "Move images");
            InsertSpriteFrames(values, insertion, "Move images");
        }
        using var captured = SessionHistory!.CaptureOperationsAtomic(Apply);
        captured.Publish(SessionHistory, "Move images");
    }
    public bool CanPasteSpriteFrames(string text) => CanEditSpriteCollection && !string.IsNullOrWhiteSpace(text) && text.Length <= 16 * 1024 * 1024
        && SpriteClipboard.CanPaste(text, typeof(List<SpriteInfo>), typeof(List<SpriteInfo>), typeof(List<SpriteInfo>));
    public void PasteSpriteFrames(string text)
    {
        if (!CanPasteSpriteFrames(text)) throw new InvalidOperationException("The clipboard does not contain compatible sprites.");
        var result = SpriteClipboard.DeserializeCopiedData(text, AuthoredAsset, typeof(List<SpriteInfo>));
        if (result.Items.FirstOrDefault() is not { Processor: AssetPropertyPasteProcessor, Data: List<SpriteInfo> values } || values.Count == 0) return;
        var detector = new UnloadableDetector(); detector.Visit(values);
        if (detector.Found) throw new InvalidOperationException("The copied sprites contain unresolved types.");
        InsertSpriteFrames(values, -1, $"Paste {values.Count} images");
    }
    private void InsertSpriteFrames(IReadOnlyList<SpriteInfo> values, int insertion, string name)
    {
        using var nativeSelection = BeginNativeSpriteSelection();
        var sprites = EditableSprites();
        if (insertion < 0) insertion = SpriteSettings!.Sprites.Count;
        if (insertion > SpriteSettings!.Sprites.Count) throw new ArgumentOutOfRangeException(nameof(insertion));
        var selected = new List<Stride.Core.Reflection.ItemId>();
        ApplyInheritedAssetMutation(() =>
        {
            foreach (var value in values)
            {
                AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
                var index = new NodeIndex(insertion++); sprites.Add(value, index);
                selected.Add(sprites.IndexToId(index));
            }
        }, name);
        selectedSpriteIds.Clear(); foreach (var id in selected) AddNativeSelectedSprite(id);
        SpriteSelectionVersion++;
    }
}
