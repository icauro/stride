using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal event Action<bool>? NativeSpriteSelectionChanged;
    private int nativeSpriteSelectionDepth;
    private bool nativeSpriteSelectionAdded;
    internal ItemId[] CaptureNativeSpriteScopeIds() => selectedSpriteIds.ToArray();
    internal void RestoreNativeSpriteSelection(IEnumerable<ItemId> ids)
    {
        EnsureSpriteSettingsGraph();
        var valid = spriteSettingsFrames.Select(frame => frame.Id).ToHashSet();
        var actual = ids.Where(valid.Contains).Distinct().ToArray();
        selectedSpriteIds.Clear(); spriteSelectionOrder.Clear();
        foreach (var id in actual) { selectedSpriteIds.Add(id); spriteSelectionOrder.Add(id); }
        spriteSelectionAnchor = actual.Length == 0 ? null : actual[^1];
        SpriteSelectionVersion++; NativeSpriteSelectionChanged?.Invoke(false);
    }
    internal void ClearNativeSpriteSelection()
    {
        if (selectedSpriteIds.Count == 0) return;
        selectedSpriteIds.Clear(); spriteSelectionOrder.Clear(); spriteSelectionAnchor = null;
        SpriteSelectionVersion++; NativeSpriteSelectionChanged?.Invoke(false);
    }
    private bool AddNativeSelectedSprite(ItemId id)
    {
        if (!selectedSpriteIds.Add(id)) return false;
        if (nativeSpriteSelectionDepth != 0) nativeSpriteSelectionAdded = true;
        else NativeSpriteSelectionChanged?.Invoke(true);
        return true;
    }
    private IDisposable BeginNativeSpriteSelection()
    {
        if (nativeSpriteSelectionDepth == 0) SessionHistory?.BeforeSelectionChange?.Invoke();
        nativeSpriteSelectionDepth++;
        return new NativeSpriteSelectionScope(this);
    }
    private sealed class NativeSpriteSelectionScope(AssetMetadataDocument owner) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (--owner.nativeSpriteSelectionDepth != 0) return;
            var added = owner.nativeSpriteSelectionAdded; owner.nativeSpriteSelectionAdded = false;
            owner.NativeSpriteSelectionChanged?.Invoke(added);
        }
    }
}
