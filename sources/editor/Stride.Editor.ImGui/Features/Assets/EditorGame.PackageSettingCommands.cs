using Stride.Core.Assets;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys;
using Stride.Core.Diagnostics;
using Stride.Core.Presentation.Quantum.Presenters;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private Action? pendingPackageSettingCommand;
    private EditorPresenterTarget? pendingPackageSettingTarget;
    private void CancelPendingPackageSettingCommand()
    {
        var target = pendingPackageSettingTarget;
        pendingPackageSettingCommand = null; pendingPackageSettingTarget = null;
        if (target != null) packageCommandCleanup.Add(target.Dispose);
    }
    private readonly List<Action> packageCommandCleanup = [];
    private string? packageCommandCleanupError;
    private bool PumpPackageCommandCleanup()
    {
        try
        {
            WindowsOwnedAssetFileWriter.RetryFailedWrites();
            WindowsOwnedDirectory.RetryFailedCreations();
            while (packageCommandCleanup.Count != 0) { packageCommandCleanup[0](); packageCommandCleanup.RemoveAt(0); }
            packageCommandCleanupError = null; return true;
        }
        catch (Exception error)
        {
            var message = error.ToString();
            if (packageCommandCleanupError != message) { packageCommandCleanupError = message; GlobalLogger.GetLogger("EditorExtensions").Error("Native package command cleanup requires recovery.", error); }
            _ = RoutePreviewGraphicsFailure(error);
            return false;
        }
    }
    private void QueuePackageSettingCommand(Package package, PackageSettingAccessor accessor, INodePresenter presenter, string commandName, object? parameter)
    {
        if (pendingPackageSettingCommand != null || IsEditorExtensionCommandRunning || IsAuthoringPipelineUpdating)
            throw new InvalidOperationException("The editor cannot start another native command.");
        var tree = packageSettingTrees[(package, accessor.Key)];
        SettingProjection? FindProjection(IEnumerable<SettingProjection> projections)
        {
            foreach (var projection in projections)
            {
                if (projection.Presentation.Owns(presenter)) return projection;
                if (FindProjection(projection.Children.Values) is { } child) return child;
            }
            return null;
        }
        var projection = tree.KeyPresentation?.Owns(presenter) == true ? null : FindProjection(tree.Projections.Values)
            ?? throw new InvalidOperationException("The native package presenter is no longer available.");
        void ValidateSource()
        {
            if (workspace == null || SessionAuthoringHistory == null || !workspace.Session.Packages.Contains(package)
                || package.State < PackageState.AssetsReady || !package.UserSettings.Profile.CanReadRegisteredValue(accessor.Key)
                || !packageSettingTrees.TryGetValue((package, accessor.Key), out var currentTree) || !ReferenceEquals(currentTree, tree)
                || tree.Version != (SessionAuthoringHistory.Extensions?.Version ?? -1)
                || !SameSettingValue(accessor, accessor.Get(package), tree.Source))
                throw new InvalidOperationException("The native package presenter changed or is read-only.");
            var command = presenter.Commands.SingleOrDefault(candidate => candidate.Name == commandName)
                ?? throw new InvalidOperationException("The native package command is no longer registered.");
            if (!command.CanExecute([presenter], parameter)) throw new InvalidOperationException("The native package command cannot use this parameter.");
        }
        ValidateSource();
        if (commandName == "CreateNewInstance" && parameter is Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands.AbstractNodeEntry entry
            && entry.IsMatchingValue(presenter.Value!)) return;
        ValidatePendingPropertyStrings();
        ValidateSource();
        if (commandName == "CreateNewInstance" && parameter is Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands.AbstractNodeEntry latestEntry
            && latestEntry.IsMatchingValue(presenter.Value!)) return;
        var pins = new List<EditorPresenterTarget>();
        try
        {
            if (projection != null)
            {
                pins.Add(tree.KeyPresentation!.AcquireTarget(tree.KeyPresentation.RootPresenter));
                var ancestors = new Stack<SettingProjection>();
                for (var parent = projection.Parent; parent != null; parent = parent.Parent) ancestors.Push(parent);
                foreach (var parent in ancestors) pins.Add(parent.Presentation.AcquireTarget(parent.Presentation.RootPresenter));
            }
            pins.Add((projection?.Presentation ?? tree.KeyPresentation!).AcquireTarget(presenter));
        }
        catch
        {
            packageCommandCleanup.Add(() => { while (pins.Count != 0) { pins[^1].Dispose(); pins.RemoveAt(pins.Count - 1); } });
            PumpPackageCommandCleanup(); throw;
        }
        var source = new EditorPresenterTarget(presenter, () => { while (pins.Count != 0) { pins[^1].Dispose(); pins.RemoveAt(pins.Count - 1); } });
        var address = projection?.Address() ?? [];
        pendingPackageSettingTarget = source;
        pendingPackageSettingCommand = () => StartPackageSettingCommand(package, accessor, source, address, commandName, parameter);
    }
    private void DispatchPendingPackageSettingCommand()
    {
        var pending = pendingPackageSettingCommand; pendingPackageSettingCommand = null; pendingPackageSettingTarget = null;
        if (pending != null) Execute(pending);
    }
    private void StartPackageSettingCommand(Package package, PackageSettingAccessor accessor, EditorPresenterTarget source, string[][] address, string commandName, object? parameter)
    {
        PackageSettingsCommandMutation? mutation = null;
        EditorPresenterTarget? target = null;
        var transferred = false;
        try
        {
            if (workspace == null || sessionHistory == null || IsEditorExtensionCommandRunning || projectLoading != null || assetPreviewBuild != null
                || IsAuthoringPipelineUpdating || closeRequested || closePromptOpen || closingDocument != null || !workspace.Session.Packages.Contains(package))
                throw new InvalidOperationException("The editor cannot start another native command.");
            var path = PackageSettingPresentation.RelativePath(source.Presenter);
            var owner = GetPackageSettingsDocument(package, sessionHistory);
            mutation = new(package, accessor, owner, sessionHistory);
            mutation.Initialize(address);
            var presenter = mutation.CommandPresentation.Find(mutation.CommandRoot) ?? throw new InvalidOperationException("The native package command has no root presenter.");
            foreach (var step in path) presenter = presenter.Children.SingleOrDefault(child => child.Name == step)
                ?? throw new InvalidOperationException("The native package command target changed.");
            var command = presenter.Commands.SingleOrDefault(command => command.Name == commandName)
                ?? throw new InvalidOperationException("The native package command is no longer registered.");
            if (!command.CanExecute([presenter], parameter)) throw new InvalidOperationException("The native package command cannot execute with this parameter.");
            target = new(presenter, source.Dispose);
            var run = new ExtensionCommandRun(workspace, sessionHistory, owner, target, mutation, command, parameter);
            transferred = true;
            BeginEditorExtensionCommandRun(run);
        }
        catch
        {
            if (!transferred)
            {
                if (mutation != null) packageCommandCleanup.Add(mutation.Dispose);
                if (target != null) packageCommandCleanup.Add(target.Dispose); else packageCommandCleanup.Add(source.Dispose);
                PumpPackageCommandCleanup();
            }
            throw;
        }
    }
    private void DrawPackageSettingCommands(Package package, PackageSettingAccessor accessor, INodePresenter? presenter)
    {
        if (presenter == null || presenter.Commands.Count == 0) return;
        // WPF command eligibility is the command's CanExecute, not the editing
        // state of the value control. Read-only inputs can still own commands.
        if (Gui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && Gui.IsMouseReleased(ImGuiMouseButton.Right))
            Gui.OpenPopup("##PackageSettingCommands");
        if (!Gui.BeginPopup("##PackageSettingCommands")) return;
        try
        {
            foreach (var command in presenter.Commands)
            {
                var choices = presenter.AttachedProperties.TryGetValue(AbstractNodeEntryData.Key, out var entries)
                    ? entries.Where(entry => command.CanExecute([presenter], entry)).OrderBy(entry => entry.Order).ThenBy(entry => entry.DisplayValue).ToArray() : [];
                if (choices.Length != 0)
                {
                    if (!BeginNativeMenu(command.Name, !IsAuthoringPipelineUpdating)) continue;
                    foreach (var choice in choices)
                        if (DrawNativeMenuItem(choice.DisplayValue)) Execute(() => QueuePackageSettingCommand(package, accessor, presenter, command.Name, choice));
                    Gui.EndMenu();
                }
                else if (DrawNativeMenuItem(command.Name, "", false, !IsAuthoringPipelineUpdating && command.CanExecute([presenter], null)))
                    Execute(() => QueuePackageSettingCommand(package, accessor, presenter, command.Name, null));
            }
        }
        finally { Gui.EndPopup(); }
    }
}
