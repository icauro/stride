namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void RemoveNativeImportCollectionItem(AssetMetadataDocument owner, AssetImportSettingItem item)
    {
        ValidatePendingPropertyStrings();
        if (IsAuthoringPipelineUpdating || !IsCurrentMetadataControlOwner(owner) || !owner.CanEditImportItem(item)
            || !owner.CanRemoveImportCollection(item.Field)
            || sessionHistory?.Registry.Find(owner.AuthoredAsset.Id) is not { IsPublished: true } state || !ReferenceEquals(state.Document, owner))
            throw new InvalidOperationException("The import item owner is no longer editable.");
        var target = owner.AcquireImportItemCommandTarget(item); var transferred = false;
        try
        {
            var command = target?.Presenter.Commands.FirstOrDefault(command => command.Name == "RemoveItem");
            if (command != null) { StartEditorExtensionCommand(owner, target!, command); transferred = true; }
            else owner.RemoveImportCollectionItem(item);
        }
        finally
        {
            if (!transferred && target != null)
                try { target.Dispose(); } catch { packageCommandCleanup.Add(target.Dispose); throw; }
        }
    }
}
