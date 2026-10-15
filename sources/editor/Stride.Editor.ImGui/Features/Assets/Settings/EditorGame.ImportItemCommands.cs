using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawImportItemCommands(AssetMetadataDocument owner, AssetImportSettingItem item)
    {
        var target = owner.AcquireImportItemCommandTarget(item);
        if (target == null) return;
        var value = item.Value;
        try
        {
            foreach (var command in NodePresenterCombination.Commands(target.Presenters))
                if (DrawNativeMenuItem(command.Name, "", false,
                    !IsAuthoringPipelineUpdating && command.CanExecute(target.Presenters, null)))
                    Execute(() => StartImportItemCommand(owner, item, value, command));
        }
        finally
        {
            try { target.Dispose(); }
            catch { packageCommandCleanup.Add(target.Dispose); throw; }
        }
    }

    private void StartImportItemCommand(AssetMetadataDocument owner, AssetImportSettingItem item,
        object? value, INodePresenterCommand command)
    {
        if (IsAuthoringPipelineUpdating) throw new InvalidOperationException("The editor cannot start another native command.");
        ValidatePendingPropertyStrings();
        owner.CommitEdits();
        if (!owner.CanCopyImportItem(item) || !ContentAssetReference.AreEqual(item.Value, value))
            throw new InvalidOperationException("The authored collection item changed before the command.");
        var target = owner.AcquireImportItemCommandTarget(item)
            ?? throw new InvalidOperationException("The authored collection item is no longer available.");
        var transferred = false;
        try
        {
            if (!NodePresenterCombination.Commands(target.Presenters).Contains(command)
                || !command.CanExecute(target.Presenters, null))
                throw new InvalidOperationException("The native item command is no longer available.");
            transferred = true;
            StartEditorExtensionCommand(owner, target, command);
        }
        finally
        {
            if (!transferred)
                try { target.Dispose(); }
                catch { packageCommandCleanup.Add(target.Dispose); throw; }
        }
    }
}
