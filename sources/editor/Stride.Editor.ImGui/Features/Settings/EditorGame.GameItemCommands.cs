using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawGameItemCommands(AssetMetadataDocument owner, GameSettingItem item)
    {
        if (!owner.CanCopyGameSettingItem(item)) return;
        var index = item.Index;
        var value = item.Value;
        var target = owner.AcquireGameItemCommandTarget(item);
        if (target == null) return;
        try
        {
            var accessor = target.Presenter.GetNodeAccessor();
            if (!owner.CanCopyGameSettingItem(item) || item.Index != index
                || !ReferenceEquals(accessor.Node, item.Collection) || accessor.Index != index
                || !ContentAssetReference.AreEqual(item.Value, value)) return;
            foreach (var command in NodePresenterCombination.Commands(target.Presenters))
                if (DrawNativeMenuItem(command.Name, "", false,
                    !IsAuthoringPipelineUpdating && command.CanExecute(target.Presenters, null)))
                    Execute(() => StartGameItemCommand(owner, item, value, command));
        }
        finally
        {
            try { target.Dispose(); }
            catch { packageCommandCleanup.Add(target.Dispose); throw; }
        }
    }

    private void StartGameItemCommand(AssetMetadataDocument owner, GameSettingItem item, object? value, INodePresenterCommand command)
    {
        if (IsAuthoringPipelineUpdating) throw new InvalidOperationException("The editor cannot start another native command.");
        ValidatePendingPropertyStrings();
        owner.CommitEdits();
        if (!owner.CanCopyGameSettingItem(item) || !ContentAssetReference.AreEqual(item.Value, value))
            throw new InvalidOperationException("The authored settings item changed before the command.");
        var target = owner.AcquireGameItemCommandTarget(item)
            ?? throw new InvalidOperationException("The authored settings item is no longer available.");
        var transferred = false;
        try
        {
            var accessor = target.Presenter.GetNodeAccessor();
            if (!owner.CanCopyGameSettingItem(item) || !ReferenceEquals(accessor.Node, item.Collection)
                || accessor.Index != item.Index || !ContentAssetReference.AreEqual(item.Value, value)
                || !NodePresenterCombination.Commands(target.Presenters).Contains(command)
                || !command.CanExecute(target.Presenters, null))
                throw new InvalidOperationException("The native settings item command is no longer available.");
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
