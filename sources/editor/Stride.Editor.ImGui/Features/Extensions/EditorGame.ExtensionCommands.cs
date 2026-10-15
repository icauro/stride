using Stride.Core.Diagnostics;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private ExtensionCommandRun? extensionCommand;
    private bool IsEditorExtensionCommandRunning => extensionCommand != null || pendingPackageSettingCommand != null
        || pendingPackageSettingTarget != null || packageCommandCleanup.Count != 0
        || WindowsOwnedDirectory.HasFailedCreations || WindowsOwnedAssetFileWriter.HasFailedWrites;
    private sealed class ExtensionCommandRun(ProjectWorkspace workspace, EditorSessionHistory history, IEditorDocument owner,
        EditorPresenterTarget target, IEditorCommandMutation mutation, INodePresenterCommand command, object? parameter = null)
    {
        internal readonly ProjectWorkspace Workspace = workspace;
        internal readonly EditorSessionHistory History = history;
        internal readonly IEditorDocument Owner = owner;
        internal readonly EditorPresenterTarget Target = target;
        internal readonly IEditorCommandMutation Mutation = mutation;
        internal readonly object? Parameter = parameter;
        internal readonly INodePresenterCommand Command = command;
        internal readonly EditorCommandSynchronizationContext Actor = new();
        internal readonly CancellationTokenSource Cancellation = new();
        internal Task? Task;
        internal bool Settled, MutationReleased, TargetReleased;
        internal string? LastError;
        internal Exception? ActorFailure;
    }
    private void StartEditorExtensionCommand(IAssetTagDocument owner, IGraphNode node, INodePresenterCommand command, object? parameter = null)
    {
        if (workspace == null || sessionHistory == null || extensionCommand != null || projectLoading != null || assetPreviewBuild != null
            || IsAuthoringPipelineUpdating || closeRequested || closePromptOpen || closingDocument != null)
            throw new InvalidOperationException("The editor cannot start another native command.");
        if (node is not IMemberNode member) throw new InvalidOperationException("The command requires a native property accessor.");
        ValidatePendingPropertyStrings();
        var target = owner switch
        {
            AssetMetadataDocument metadata => metadata.AcquireExtensionCommandTarget(member),
            EntityHierarchyDocument hierarchy => hierarchy.AcquireExtensionCommandTarget(member),
            ProceduralAssetDocument procedural => procedural.AcquireExtensionCommandTarget(member),
            _ => null,
        } ?? throw new InvalidOperationException("The actual command presenter is no longer available.");
        StartEditorExtensionCommand(owner, target, command, parameter);
    }
    private void StartEditorExtensionCommand(IAssetTagDocument owner, EditorPresenterTarget target, INodePresenterCommand command, object? parameter = null,
        Func<IEditorCommandMutation>? createMutation = null)
    {
        IEditorCommandMutation? mutation = null;
        var transferred = false;
        try
        {
            if (workspace == null || sessionHistory == null || extensionCommand != null || projectLoading != null || assetPreviewBuild != null
                || IsAuthoringPipelineUpdating || closeRequested || closePromptOpen || closingDocument != null)
                throw new InvalidOperationException("The editor cannot start another native command.");
            if (!NodePresenterCombination.Commands(target.Presenters).Contains(command) || !command.CanExecute(target.Presenters, parameter)) throw new InvalidOperationException("The command is no longer available.");
            ValidatePendingPropertyStrings();
            if (!NodePresenterCombination.Commands(target.Presenters).Contains(command) || !command.CanExecute(target.Presenters, parameter)) throw new InvalidOperationException("The command is no longer available.");
            mutation = createMutation?.Invoke() ?? (owner switch
            {
                AssetMetadataDocument metadata => new EditorAssetCommandMutation(metadata.BeginExtensionCommandMutation()),
                EntityHierarchyDocument hierarchy => new EditorAssetCommandMutation(hierarchy.BeginExtensionCommandMutation()),
                ProceduralAssetDocument procedural => new EditorAssetCommandMutation(procedural.BeginExtensionCommandMutation()),
                _ => throw new InvalidOperationException("The command has no canonical native journal."),
            });
            var run = new ExtensionCommandRun(workspace, sessionHistory, owner, target, mutation, command, parameter);
            transferred = true;
            BeginEditorExtensionCommandRun(run);
        }
        catch
        {
            if (!transferred)
            {
                if (mutation != null) packageCommandCleanup.Add(mutation.Dispose);
                packageCommandCleanup.Add(target.Dispose);
            }
            throw;
        }
    }
    private void BeginEditorExtensionCommandRun(ExtensionCommandRun run)
    {
        extensionCommand = run;
        try
        {
            run.History.Extensions?.SetCommandCancellation(run.Cancellation.Token);
            run.Actor.Run(() => run.Task = InvokeEditorExtensionCommand(run));
        }
        catch (Exception error) { run.Task = Task.FromException(error); }
    }
    private static async Task InvokeEditorExtensionCommand(ExtensionCommandRun run)
    {
        var nodes = run.Target.Presenters;
        var prepared = await run.Command.PreExecute(nodes, run.Parameter);
        run.Cancellation.Token.ThrowIfCancellationRequested();
        foreach (var node in nodes) { await run.Command.Execute(node, run.Parameter, prepared); run.Cancellation.Token.ThrowIfCancellationRequested(); }
        await run.Command.PostExecute(nodes, run.Parameter);
        run.Cancellation.Token.ThrowIfCancellationRequested();
    }
    private void PumpEditorExtensionCommands()
    {
        if (!PumpPackageCommandCleanup()) return;
        var run = extensionCommand;
        if (run == null) return;
        Exception? failure = null;
        try
        {
            run.Actor.Pump();
            if (run.Task?.IsCompleted != true || run.Actor.HasPendingCallbacks) return;
            if (!run.Settled)
            {
                try
                {
                    if (run.ActorFailure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(run.ActorFailure).Throw();
                    run.Task.GetAwaiter().GetResult();
                    if (!ReferenceEquals(workspace, run.Workspace) || !ReferenceEquals(sessionHistory, run.History)) throw new InvalidOperationException("The command's canonical session changed.");
                    run.Mutation.Commit(run.Command.Name);
                }
                catch (Exception error)
                {
                    failure = error;
                    try { run.Mutation.Rollback(); }
                    catch (Exception compensation) { failure = new AggregateException(error, compensation); throw failure; }
                    if (error is not OperationCanceledException) GlobalLogger.GetLogger("EditorExtensions").Error("Native property command failed.", error);
                }
                run.Settled = true;
            }
            if (!run.MutationReleased) { run.Mutation.Dispose(); run.MutationReleased = true; }
            if (!run.TargetReleased) { run.Target.Dispose(); run.TargetReleased = true; }
            run.History.Extensions?.SetCommandCancellation(CancellationToken.None);
            run.Cancellation.Dispose(); extensionCommand = null;
        }
        catch (Exception error)
        {
            if (!run.Settled) run.ActorFailure ??= error;
            failure = failure == null || ReferenceEquals(failure, error) ? error : new AggregateException(failure, error);
            try { run.Cancellation.Cancel(); }
            catch (Exception cancellation) { failure = new AggregateException(failure, cancellation); }
            var message = failure.ToString();
            if (run.LastError != message) { run.LastError = message; GlobalLogger.GetLogger("EditorExtensions").Error("Native command cleanup requires recovery.", failure); }
        }
        // Mutation compensation and ownership bookkeeping precede propagation.
        // Recovery starts at the existing failed-tick boundary, after GUI scopes.
        if (failure != null) _ = RoutePreviewGraphicsFailure(failure);
    }
    private void StopEditorExtensionCommands()
    {
        CancelPendingPackageSettingCommand(); extensionCommand?.Cancellation.Cancel();
    }
    private void DrainEditorExtensionCommands()
    {
        StopEditorExtensionCommands(); PumpEditorExtensionCommands();
        if (IsEditorExtensionCommandRunning) throw new InvalidOperationException("The native command still owns canonical graph callbacks; wait for cancellation before disposing its session.");
    }
    private void DrawEditorExtensions()
    {
        if (!IsEditorGpuRetirementPending) sessionHistory?.Extensions?.DrawFrame();
    }
}
