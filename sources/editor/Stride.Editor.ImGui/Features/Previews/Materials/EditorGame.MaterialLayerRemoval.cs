namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void RemoveNativeMaterialLayer(AssetMetadataDocument owner, MaterialSettingLayer layer)
    {
        ValidatePendingPropertyStrings();
        if (IsAuthoringPipelineUpdating || !IsCurrentMetadataControlOwner(owner) || !owner.CanRemoveMaterialLayer(layer)
            || sessionHistory?.Registry.Find(owner.AuthoredAsset.Id) is not { IsPublished: true } state || !ReferenceEquals(state.Document, owner))
            throw new InvalidOperationException("The material layer owner is no longer editable.");
        var target = owner.AcquireMaterialLayerCommandTarget(layer); var transferred = false;
        try
        {
            var command = target?.Presenter.Commands.FirstOrDefault(command => command.Name == "RemoveItem");
            if (command != null) { StartEditorExtensionCommand(owner, target!, command); transferred = true; }
            else owner.RemoveMaterialLayer(layer);
        }
        finally
        {
            if (!transferred && target != null)
                try { target.Dispose(); } catch { packageCommandCleanup.Add(target.Dispose); throw; }
        }
    }
}
