using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawMaterialLayerCommands(AssetMetadataDocument owner, MaterialSettingLayer layer)
    {
        var target = owner.AcquireMaterialLayerCommandTarget(layer);
        if (target == null) return;
        try
        {
            foreach (var command in NodePresenterCombination.Commands(target.Presenters))
                if (DrawNativeMenuItem(command.Name, "", false,
                    !IsAuthoringPipelineUpdating && command.CanExecute(target.Presenters, null)))
                    Execute(() => StartMaterialLayerCommand(owner, layer, command));
        }
        finally
        {
            try { target.Dispose(); }
            catch { packageCommandCleanup.Add(target.Dispose); throw; }
        }
    }

    private void StartMaterialLayerCommand(AssetMetadataDocument owner, MaterialSettingLayer layer, INodePresenterCommand command)
    {
        if (IsAuthoringPipelineUpdating) throw new InvalidOperationException("The editor cannot start another native command.");
        ValidatePendingPropertyStrings();
        owner.CommitEdits();
        var target = owner.AcquireMaterialLayerCommandTarget(layer)
            ?? throw new InvalidOperationException("The authored material layer is no longer available.");
        var transferred = false;
        try
        {
            if (!NodePresenterCombination.Commands(target.Presenters).Contains(command)
                || !command.CanExecute(target.Presenters, null))
                throw new InvalidOperationException("The native layer command is no longer available.");
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
