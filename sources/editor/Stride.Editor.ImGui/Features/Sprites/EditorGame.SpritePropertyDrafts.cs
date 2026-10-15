using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool IsSpriteSettingDraftCurrent(AssetMetadataDocument owner, SpriteSettingField field)
        => IsCurrentMetadataControlOwner(owner)
        && sessionHistory?.Registry.Find(owner.AuthoredAsset.Id) is { IsPublished: true } state && ReferenceEquals(state.Document, owner)
        && owner.IsCurrentSpriteSettingField(field);
    private bool CanEditSpriteSettingDraft(AssetMetadataDocument owner, SpriteSettingField field)
        => !IsAuthoringPipelineUpdating && IsSpriteSettingDraftCurrent(owner, field) && owner.CanEditSpriteSetting(field);
    private object?[] ReadSpriteSettingDraft(AssetMetadataDocument owner, SpriteSettingField field)
        => [.. owner.CaptureSpriteSettingDraft(field), owner.Path, sessionHistory?.Registry.Find(owner.AuthoredAsset.Id)?.Generation,
            sessionHistory?.Extensions?.Version];
    private IDisposable AcquireSpriteSettingDraft(AssetMetadataDocument owner, SpriteSettingField field)
    {
        var leases = new List<IDisposable>();
        try
        {
            if (!IsSpriteSettingDraftCurrent(owner, field)) throw new InvalidOperationException("The sprite property owner is no longer current.");
            leases.Add(AcquirePropertyStringOwner(owner));
            if (owner.AcquireExtensionCommandTarget(field.Node) is { } target) leases.Add(target);
            return new PropertyStringOwners(leases, [owner]);
        }
        catch (Exception failure)
        {
            var cleanup = new PropertyStringOwners(leases, [owner]);
            try { cleanup.Dispose(); }
            catch (Exception release) { packageCommandCleanup.Add(cleanup.Dispose); throw new AggregateException(failure, release); }
            throw;
        }
    }
    private void CommitSpriteSettingDraft(AssetMetadataDocument owner, SpriteSettingField field, object value)
    {
        if (!CanEditSpriteSettingDraft(owner, field)) throw new InvalidOperationException("The sprite property is no longer editable.");
        if (Equals(field.Node.Retrieve(), value)) return;
        UseTextureSettingsCommandContext();
        owner.UpdateSpriteSetting(field, value);
        owner.EndSpriteSettingEdit();
    }
    private void SelectSpriteFrameAfterPendingEdits(AssetMetadataDocument owner, ItemId? id, bool toggle = false, bool range = false)
    {
        ValidatePendingPropertyStrings();
        owner.SelectSpriteFrame(id, toggle, range);
    }
}
