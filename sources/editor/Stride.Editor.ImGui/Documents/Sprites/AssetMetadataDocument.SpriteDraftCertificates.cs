namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal bool IsCurrentSpriteSettingField(SpriteSettingField field)
    {
        EnsureSpriteSettingsGraph();
        if (!TryGetSpriteSettingOwner(field, out var frame)) return false;
        if (frame == null) return true;
        var sprites = (Stride.Core.Assets.Quantum.IAssetObjectNode)spriteSettingsGraph!.RootNode[nameof(Stride.Assets.Sprite.SpriteSheetAsset.Sprites)].Target!;
        return sprites.HasId(frame.Id) && ReferenceEquals(sprites.Retrieve(sprites.IdToIndex(frame.Id)), frame.Value);
    }
    internal object?[] CaptureSpriteSettingDraft(SpriteSettingField field, bool includeSelection = true)
    {
        if (!IsCurrentSpriteSettingField(field) || !TryGetSpriteSettingOwner(field, out var frame))
            throw new InvalidOperationException("The authored sprite property is no longer available.");
        return [field.Node.Retrieve(), field.Node.Parent.Retrieve(), field.Node, EditRevision, SpriteSettings!.Type,
            frame?.Id, frame?.Value, frame?.Value.Source,
            frame != null && IsSpriteBordersLocked(frame.Id), frame?.BorderNode.Retrieve(),
            .. (includeSelection ? SelectedSpriteIds.Cast<object?>() : Enumerable.Empty<object?>())];
    }
}
