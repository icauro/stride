using Stride.Assets.Sprite;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    public void SelectAllSpriteFrames()
    {
        using var nativeSelection = BeginNativeSpriteSelection();
        EnsureSpriteSettingsGraph(); CommitEdits(); selectedSpriteIds.Clear(); SpriteSelectionVersion++;
        foreach (var frame in spriteSettingsFrames) AddNativeSelectedSprite(frame.Id);
    }
    public void SelectNextSpriteFrame(int offset)
    {
        if (SelectedSpriteId is not { } selected || spriteSettingsFrames.Count == 0) return;
        for (var index = 0; index < spriteSettingsFrames.Count; index++)
            if (spriteSettingsFrames[index].Id == selected)
            { SelectSpriteFrame(spriteSettingsFrames[(index + offset + spriteSettingsFrames.Count) % spriteSettingsFrames.Count].Id); return; }
    }
    public void DeleteSelectedSpriteFrames()
    {
        EnsureSpriteSettingsGraph(); CommitEdits();
        var selected = spriteSettingsFrames.Where(frame => selectedSpriteIds.Contains(frame.Id)).Reverse().ToArray();
        if (selected.Length == 0) return;
        var nearest = spriteSettingsFrames.Select((frame, index) => (frame, index)).Where(pair => selectedSpriteIds.Contains(pair.frame.Id)).Min(pair => pair.index);
        var sprites = EditableSprites();
        foreach (var frame in selected)
            if (!CanEditAssetSettingItem(sprites, sprites.IdToIndex(frame.Id))) throw new InvalidOperationException("This sprite cannot be edited.");
        ApplyInheritedAssetMutation(() =>
        { foreach (var frame in selected) sprites.Remove(frame.Value, sprites.IdToIndex(frame.Id)); }, "Delete sprites");
        SelectSpriteFrame(spriteSettingsFrames.Count == 0 ? null : spriteSettingsFrames[Math.Min(nearest, spriteSettingsFrames.Count - 1)].Id);
    }
    public void DuplicateSelectedSpriteFrames()
    {
        using var nativeSelection = BeginNativeSpriteSelection();
        var sprites = EditableSprites();
        var selected = SelectedSpriteIds.Select(id => spriteSettingsFrames.First(frame => frame.Id == id)).ToArray();
        var ids = new List<ItemId>();
        ApplyInheritedAssetMutation(() =>
        {
        foreach (var frame in selected)
        {
            var value = AssetCloner.Clone(frame.Value); AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
            var index = new NodeIndex(SpriteSettings!.Sprites.Count); sprites.Add(value, index);
            ids.Add(sprites.IndexToId(index));
        }
        }, $"Duplicate {selected.Length} images");
        selectedSpriteIds.Clear(); foreach (var id in ids) AddNativeSelectedSprite(id);
        SpriteSelectionVersion++;
    }
    public void MoveSelectedSpriteFrames(int offset)
    {
        using var nativeSelection = BeginNativeSpriteSelection();
        if (offset is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(offset));
        var sprites = EditableSprites();
        var selected = spriteSettingsFrames.Select((frame, index) => (frame, index))
            .Where(pair => selectedSpriteIds.Contains(pair.frame.Id)).OrderBy(pair => pair.index * -offset).ToArray();
        var moving = selectedSpriteIds.ToHashSet();
        foreach (var pair in selected)
        {
            var target = pair.index + offset;
            if (target < 0 || target >= spriteSettingsFrames.Count || selectedSpriteIds.Contains(spriteSettingsFrames[target].Id)
                && !moving.Contains(spriteSettingsFrames[target].Id)) moving.Remove(pair.frame.Id);
        }
        foreach (var pair in selected)
            if (moving.Contains(pair.frame.Id) && !CanEditAssetSettingItem(sprites, sprites.IdToIndex(pair.frame.Id)))
                throw new InvalidOperationException("This sprite cannot be edited.");
        ApplyInheritedAssetMutation(() =>
        { foreach (var pair in selected) if (moving.Contains(pair.frame.Id)) MoveSpriteFrameCore(sprites, pair.frame, pair.index + offset); }, "Move sprites");
        selectedSpriteIds.Clear(); foreach (var pair in selected) AddNativeSelectedSprite(pair.frame.Id);
        SpriteSelectionVersion++;
    }
    public void EditSpriteFrames(string name, Action edit) { CommitEdits(); using (CreateDocumentTransaction(name)) edit(); }
    public void InsertSpriteFramesBefore(IReadOnlyCollection<ItemId> ids, ItemId? target, bool after)
    {
        using var nativeSelection = BeginNativeSpriteSelection();
        var sprites = EditableSprites();
        var moving = spriteSettingsFrames.Where(frame => ids.Contains(frame.Id)).ToArray();
        if (moving.Length == 0 || target is { } targetId && (ids.Contains(targetId) || !sprites.HasId(targetId))) return;
        foreach (var frame in moving)
            if (!CanEditAssetSettingItem(sprites, sprites.IdToIndex(frame.Id))) throw new InvalidOperationException("This sprite cannot be edited.");
        var order = spriteSettingsFrames.Where(frame => !ids.Contains(frame.Id)).Select(frame => frame.Id).ToList();
        var insertion = target is { } beforeId ? order.IndexOf(beforeId) + (after ? 1 : 0) : order.Count;
        order.InsertRange(insertion, moving.Select(frame => frame.Id));
        ApplyInheritedAssetMutation(() =>
        {
            for (var index = 0; index < order.Count; index++)
                if (sprites.IdToIndex(order[index]).Int != index)
                {
                    // Move selected items only: unselected rows retain their original overrides.
                    var id = order[index];
                    if (ids.Contains(id)) MoveSpriteFrameCore(sprites, spriteSettingsFrames.First(frame => frame.Id == id), index);
                    else
                    {
                        var current = sprites.IndexToId(new NodeIndex(index));
                        var selected = spriteSettingsFrames.First(frame => frame.Id == current);
                        if (!ids.Contains(selected.Id)) continue;
                        MoveSpriteFrameCore(sprites, selected, order.IndexOf(selected.Id));
                        index--;
                    }
                }
        }, "Move images");
        selectedSpriteIds.Clear(); foreach (var frame in moving) AddNativeSelectedSprite(frame.Id);
        SpriteSelectionVersion++;
    }
}
