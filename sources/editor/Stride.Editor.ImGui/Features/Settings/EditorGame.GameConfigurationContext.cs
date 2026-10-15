using Stride.Core.Presentation.Quantum.Presenters;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawGameConfigurationContext(AssetMetadataDocument owner, GameSettingConfiguration configuration,
        Action<Action> schedule, bool canRemove)
    {
        if (Gui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && Gui.IsMouseReleased(ImGuiMouseButton.Right))
            Gui.OpenPopup("ConfigurationActions");
        if (!Gui.BeginPopup("ConfigurationActions")) return;
        try
        {
            if (DrawNativeMenuItem(EditorLocalization.Widget("Copy", "Menu"), "", false, owner.CanCopyGameConfiguration(configuration)))
                Execute(() => Gui.SetClipboardText(owner.CopyGameConfiguration(configuration)));
            var text = Gui.GetClipboardTextS() ?? "";
            var canPaste = !IsAuthoringPipelineUpdating && owner.CanPasteGameConfiguration(configuration, text);
            if (DrawNativeMenuItem(EditorLocalization.Widget("Paste", "Menu"), "", false, canPaste)) schedule(() => RunGameConfigurationAction(owner, configuration,
                () => owner.CanPasteGameConfiguration(configuration, text), () => owner.PasteGameConfiguration(configuration, text, false)));
            if (DrawNativeMenuItem("Replace", "", false, canPaste)) schedule(() => RunGameConfigurationAction(owner, configuration,
                () => owner.CanPasteGameConfiguration(configuration, text), () => owner.PasteGameConfiguration(configuration, text, true)));
            if (DrawNativeMenuItem(EditorLocalization.Widget("Reset to base value", "Menu"), "", false, !IsAuthoringPipelineUpdating && owner.CanResetGameConfiguration(configuration)))
                schedule(() => RunGameConfigurationAction(owner, configuration,
                    () => owner.CanResetGameConfiguration(configuration), () => owner.ResetGameConfiguration(configuration)));
            if (DrawNativeMenuItem(EditorLocalization.Widget("Remove", "Button"), "", false, !IsAuthoringPipelineUpdating && canRemove && owner.CanRemoveGameConfiguration(configuration)))
                schedule(() => RunGameConfigurationAction(owner, configuration,
                    () => owner.CanRemoveGameConfiguration(configuration), () => owner.RemoveGameConfiguration(configuration)));
            var target = owner.AcquireGameConfigurationCommandTarget(configuration);
            if (target == null) return;
            try
            {
                if (!owner.IsGameConfigurationCommandTargetCurrent(configuration, target)) return;
                foreach (var command in NodePresenterCombination.Commands(target.Presenters))
                    if (DrawNativeMenuItem(command.Name, "", false,
                        !IsAuthoringPipelineUpdating && command.CanExecute(target.Presenters, null)))
                        Execute(() => StartGameConfigurationCommand(owner, configuration, command));
            }
            finally
            {
                try { target.Dispose(); }
                catch { packageCommandCleanup.Add(target.Dispose); throw; }
            }
        }
        finally { Gui.EndPopup(); }
    }
    private void RunGameConfigurationAction(AssetMetadataDocument owner, GameSettingConfiguration configuration,
        Func<bool> canRun, Action action)
    {
        if (IsAuthoringPipelineUpdating || !IsCurrentMetadataControlOwner(owner) || !canRun())
            throw new InvalidOperationException("The configuration action is no longer available.");
        var generation = sessionHistory?.Registry.Find(owner.AuthoredAsset.Id)?.Generation;
        var version = owner.SessionHistory?.Extensions?.Version;
        var source = owner.GameConfigurationSource(configuration);
        using var view = new RetainedPathLease(this, AcquirePropertyStringOwner(owner));
        using var original = new RetainedPathLease(this, owner.AcquireGameConfigurationCommandTarget(configuration)
            ?? throw new InvalidOperationException("The configuration presenter changed."));
        ValidatePendingPropertyStrings();
        if (IsAuthoringPipelineUpdating || !IsCurrentMetadataControlOwner(owner)
            || generation != sessionHistory?.Registry.Find(owner.AuthoredAsset.Id)?.Generation
            || version != owner.SessionHistory?.Extensions?.Version
            || !source.SequenceEqual(owner.GameConfigurationSource(configuration)) || !canRun())
            throw new InvalidOperationException("The configuration action changed during pending validation.");
        var target = owner.AcquireGameConfigurationCommandTarget(configuration)
            ?? throw new InvalidOperationException("The configuration presenter changed during pending validation.");
        using var current = new RetainedPathLease(this, target);
        if (!owner.IsGameConfigurationCommandTargetCurrent(configuration, target))
            throw new InvalidOperationException("The configuration target changed.");
        action();
    }
    private void StartGameConfigurationCommand(AssetMetadataDocument owner, GameSettingConfiguration configuration, INodePresenterCommand command)
    {
        if (IsAuthoringPipelineUpdating || !IsCurrentMetadataControlOwner(owner))
            throw new InvalidOperationException("The editor cannot start another native command.");
        var source = owner.GameConfigurationSource(configuration);
        var generation = sessionHistory?.Registry.Find(owner.AuthoredAsset.Id)?.Generation;
        var version = owner.SessionHistory?.Extensions?.Version;
        using var view = new RetainedPathLease(this, AcquirePropertyStringOwner(owner));
        var originalTarget = owner.AcquireGameConfigurationCommandTarget(configuration)
            ?? throw new InvalidOperationException("The configuration presenter changed.");
        using var original = new RetainedPathLease(this, originalTarget);
        if (!owner.IsGameConfigurationCommandTargetCurrent(configuration, originalTarget)
            || !NodePresenterCombination.Commands(originalTarget.Presenters).Contains(command)
            || !command.CanExecute(originalTarget.Presenters, null))
            throw new InvalidOperationException("The native configuration command is no longer available.");
        ValidatePendingPropertyStrings();
        if (IsAuthoringPipelineUpdating || !IsCurrentMetadataControlOwner(owner)
            || generation != sessionHistory?.Registry.Find(owner.AuthoredAsset.Id)?.Generation
            || version != owner.SessionHistory?.Extensions?.Version
            || !source.SequenceEqual(owner.GameConfigurationSource(configuration)))
            throw new InvalidOperationException("The native configuration command changed during pending validation.");
        owner.CommitEdits();
        var target = owner.AcquireGameConfigurationCommandTarget(configuration)
            ?? throw new InvalidOperationException("The authored game configuration is no longer available.");
        var transferred = false;
        try
        {
            if (!owner.IsGameConfigurationCommandTargetCurrent(configuration, target)
                || !NodePresenterCombination.Commands(target.Presenters).Contains(command) || !command.CanExecute(target.Presenters, null))
                throw new InvalidOperationException("The native configuration command is no longer available.");
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
