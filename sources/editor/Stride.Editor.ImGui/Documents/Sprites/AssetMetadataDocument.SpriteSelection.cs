using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private readonly HashSet<ItemId> selectedSpriteIds = [];
    private readonly List<ItemId> spriteSelectionOrder = [];
    private long spriteSelectionOrderVersion = -1;
    private readonly HashSet<ItemId> lockedSpriteBorders = [];
    private ItemId? spriteSelectionAnchor;
    public IReadOnlyCollection<ItemId> SelectedSpriteIds { get { ReconcileSpriteSelectionOrder(); return spriteSelectionOrder; } }
    internal SpriteSettingFrame? LastSelectedSpriteFrame
    {
        get
        {
            EnsureSpriteSettingsGraph(); ReconcileSpriteSelectionOrder();
            return spriteSelectionOrder.Count == 0 ? null : spriteSettingsFrames.FirstOrDefault(frame => frame.Id == spriteSelectionOrder[^1]);
        }
    }
    public ItemId? SelectedSpriteId { get { if (selectedSpriteIds.Count == 1) foreach (var id in selectedSpriteIds) return id; return null; } }
    public bool IsSpriteSelected(ItemId id) => selectedSpriteIds.Contains(id);
    public bool IsSpriteBordersLocked(ItemId id) => lockedSpriteBorders.Contains(id);
    public void SetSpriteBordersLocked(ItemId id, bool locked) { if (locked) lockedSpriteBorders.Add(id); else lockedSpriteBorders.Remove(id); }
    public SpriteSettingFrame? SelectedSpriteFrame
    {
        get
        {
            EnsureSpriteSettingsGraph();
            if (SelectedSpriteId is not { } selected) return null;
            foreach (var frame in spriteSettingsFrames) if (frame.Id == selected) return frame;
            return null;
        }
    }
    public void SelectSpriteFrame(ItemId? id, bool toggle = false, bool range = false)
    {
        using var nativeSelection = BeginNativeSpriteSelection();
        EnsureSpriteSettingsGraph(); CommitEdits(); ReconcileSpriteSelectionOrder(); SpriteSelectionVersion++;
        if (id != null && !spriteSettingsFrames.Any(frame => frame.Id == id)) throw new InvalidOperationException("This sprite no longer exists.");
        if (id == null) { selectedSpriteIds.Clear(); spriteSelectionOrder.Clear(); spriteSelectionAnchor = null; return; }
        if (range && spriteSelectionAnchor == null)
            spriteSelectionAnchor = spriteSelectionOrder.Count > 0 ? spriteSelectionOrder[^1] : spriteSettingsFrames.FirstOrDefault()?.Id;
        if (range && spriteSelectionAnchor is { } anchor)
        {
            var start = -1; var end = -1;
            for (var index = 0; index < spriteSettingsFrames.Count; index++)
            { if (spriteSettingsFrames[index].Id == anchor) start = index; if (spriteSettingsFrames[index].Id == id) end = index; }
            if (start >= 0 && end >= 0)
            {
                if (!toggle)
                {
                    var keep = spriteSettingsFrames.Skip(Math.Min(start, end)).Take(Math.Abs(end - start) + 1).Select(frame => frame.Id).ToHashSet();
                    selectedSpriteIds.RemoveWhere(selected => !keep.Contains(selected));
                    spriteSelectionOrder.RemoveAll(selected => !selectedSpriteIds.Contains(selected));
                }
                for (var index = Math.Min(start, end); index <= Math.Max(start, end); index++)
                    if (AddNativeSelectedSprite(spriteSettingsFrames[index].Id)) spriteSelectionOrder.Add(spriteSettingsFrames[index].Id);
                return;
            }
        }
        if (!toggle) { selectedSpriteIds.Clear(); spriteSelectionOrder.Clear(); }
        if (toggle && selectedSpriteIds.Remove(id.Value)) spriteSelectionOrder.Remove(id.Value);
        else if (AddNativeSelectedSprite(id.Value)) spriteSelectionOrder.Add(id.Value);
        spriteSelectionAnchor = id;
    }
    internal void SetSpriteSelectionAnchor(ItemId id)
    {
        EnsureSpriteSettingsGraph();
        if (spriteSettingsFrames.Any(frame => frame.Id == id)) spriteSelectionAnchor = id;
    }
    public void CancelSpriteSettingEdit()
    {
        if (activeSpriteSetting == null) return;
        var mutation = spriteSettingMutation!;
        mutation.Rollback(); mutation.Dispose(); spriteSettingMutation = null;
        spriteLockedBorderNode = null; spriteLockedBorderStart = null;
        activeSpriteSetting = null; spriteSettingStart = null;
        spriteSettingEditTitle = null;
    }
    private void ReconcileSpriteSelection()
    {
        var hadSelection = selectedSpriteIds.Count > 0;
        var removed = selectedSpriteIds.RemoveWhere(selected => !spriteSettingsFrames.Any(frame => frame.Id == selected));
        lockedSpriteBorders.RemoveWhere(selected => !spriteSettingsFrames.Any(frame => frame.Id == selected));
        if (spriteSelectionAnchor is { } anchor && !spriteSettingsFrames.Any(frame => frame.Id == anchor)) spriteSelectionAnchor = null;
        if (hadSelection && selectedSpriteIds.Count == 0 && spriteSettingsFrames.Count > 0) AddNativeSelectedSprite(spriteSettingsFrames[0].Id);
        if (removed > 0) SpriteSelectionVersion++;
        ReconcileSpriteSelectionOrder();
    }
    private void ReconcileSpriteSelectionOrder()
    {
        if (spriteSelectionOrderVersion == SpriteSelectionVersion) return;
        spriteSelectionOrder.RemoveAll(id => !selectedSpriteIds.Contains(id));
        var known = spriteSelectionOrder.ToHashSet();
        foreach (var id in selectedSpriteIds) if (known.Add(id)) spriteSelectionOrder.Add(id);
        spriteSelectionOrderVersion = SpriteSelectionVersion;
    }
}
