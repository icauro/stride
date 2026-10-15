using Stride.Core.Assets;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly List<PlatformUpdateRuntimeTransition> pendingPlatformViewRestores = [];
    private sealed class PlatformUpdateRuntimeTransition(EditorGame game, ProjectWorkspace workspace,
        SolutionProject target, CompiledProjectContent? next, string output) : IWorkspaceRuntimeHistory
    {
        internal readonly ProjectWorkspace Workspace = workspace;
        internal readonly EditorSessionHistory? History = game.SessionAuthoringHistory;
        private readonly ProjectWorkspace.Activation before = workspace.CaptureActivation();
        private readonly CompiledProjectContent? previous = game.projectContent;
        private ProjectWorkspace.Activation? after;
        private GraphicsRecoveryState? beforeViews, afterViews;
        private Task? barrier;
        private bool applied, suspended;
        internal bool Current => ReferenceEquals(game.workspace, workspace) && ReferenceEquals(game.SessionAuthoringHistory, History)
            && workspace.CaptureActivation() == (applied ? after : before)
            && ReferenceEquals(game.projectContent, applied ? next : previous);
        internal bool CanRestoreBefore => !applied && !suspended && Current;
        public Task RetireViews()
        {
            if (!Current) throw new InvalidOperationException("The active workspace changed before platform resource retirement.");
            if (barrier is { IsCompleted: false }) return barrier;
            var state = game.CaptureWorkspaceTransitionViews(workspace);
            game.ForgetWorkspaceTransitionViews(applied ? afterViews : beforeViews);
            if (applied) afterViews = state; else beforeViews = state;
            game.ClearSelectedAssetPreview();
            barrier = game.BeginEditorGpuRetirementAsync(retireScenes: true, retireFonts: true); return barrier;
        }
        // These two native journal phases bracket membership mutation. The
        // middle state has no active project; it is never presented as a new
        // workspace and keeps the original session/history/content owner alive.
        internal void Suspend(bool forward)
        {
            if (suspended == forward) return;
            if (forward)
            {
                if (!Current || applied || barrier?.IsCompletedSuccessfully != true)
                    throw new InvalidOperationException("The actual platform resource retirement has not completed.");
                barrier.GetAwaiter().GetResult(); game.ReleaseWorkspaceTransitionNativeViews(applied ? afterViews : beforeViews);
                workspace.ClearActivation(); game.projectContent = null;
                if (previous != null && !game.retainedActiveProjectContent.Contains(previous)) game.retainedActiveProjectContent.Add(previous);
            }
            else
            {
                RequireSuspended();
                workspace.RestoreActivation(before); game.projectContent = previous;
                if (previous != null) game.retainedActiveProjectContent.Remove(previous);
                QueueRestore();
            }
            suspended = forward;
        }
        private void RequireSuspended()
        {
            if (!ReferenceEquals(game.workspace, workspace) || workspace.Package != null
                || workspace.Session.CurrentProject != null || game.projectContent != null || applied)
                throw new InvalidOperationException("The platform membership transition changed its retained activation.");
        }
        internal void Complete(bool forward)
        {
            if (applied == forward) return;
            if (forward)
            {
                if (!suspended) throw new InvalidOperationException("The platform membership transition has not retired its previous activation.");
                RequireSuspended(); workspace.SetActiveProject(target, output);
                after = workspace.CaptureActivation(); game.projectContent = next;
                if (next != null) game.retainedActiveProjectContent.Remove(next);
            }
            else
            {
                if (!Current || barrier?.IsCompletedSuccessfully != true)
                    throw new InvalidOperationException("The actual platform resource retirement has not completed.");
                barrier.GetAwaiter().GetResult(); game.ReleaseWorkspaceTransitionNativeViews(applied ? afterViews : beforeViews);
                workspace.ClearActivation(); game.projectContent = null;
                if (next != null && !game.retainedActiveProjectContent.Contains(next)) game.retainedActiveProjectContent.Add(next);
            }
            applied = forward;
            if (forward) QueueRestore();
        }
        private void QueueRestore()
        { if (!game.pendingPlatformViewRestores.Contains(this)) game.pendingPlatformViewRestores.Add(this); }
        internal void RestoreViews()
        {
            if (!Current) throw new InvalidOperationException("The active platform changed before restoring its native views.");
            var state = applied ? afterViews ?? beforeViews : beforeViews;
            if (state == null) return;
            game.ReleaseWorkspaceTransitionNativeViews(state);
            game.RebindRetiredFontSources();
            var bundle = applied ? next : previous;
            if (bundle != null && !game.RestoreSceneDocumentGraphicsRecovery(state, bundle)
                && state.PresentedDocument is { } legacyDocument
                && workspace.AssetItems.Any(item => item.Id == legacyDocument.Asset.Id)
                && game.sessionHistory?.Registry.Find(legacyDocument.Path) is { IsPublished: true, IsDisposed: false } owner
                && ReferenceEquals(owner.Document, legacyDocument))
            { state.SceneBundle = bundle; game.RestoreGraphicsRecoveryScene(state); }
            if (state.SelectedAssetPath != null && !workspace.AssetItems.Any(item => SamePath(item.FullPath.ToOSPath(), state.SelectedAssetPath))) return;
            state.PreviewCreated = false; game.RestoreGraphicsRecoveryPreview(state);
        }
    }
    private sealed class PlatformUpdateSuspendOperation(PlatformUpdateRuntimeTransition transition) : Operation
    {
        protected override void Rollback() => transition.Suspend(false);
        protected override void Rollforward() => transition.Suspend(true);
    }
    private sealed class PlatformUpdateActivationOperation(PlatformUpdateRuntimeTransition transition) : Operation
    {
        protected override void Rollback() => transition.Complete(false);
        protected override void Rollforward() => transition.Complete(true);
    }
    private void PumpPlatformUpdateViewRestores()
    {
        foreach (var transition in pendingPlatformViewRestores.ToArray())
            if (!ReferenceEquals(workspace, transition.Workspace) || !ReferenceEquals(SessionAuthoringHistory, transition.History))
                pendingPlatformViewRestores.Remove(transition);
        if (packagePlatformDrain != null || IsPackagePlatformUpdating || IsEditorGraphicsUnavailable || IsEditorGpuRetirementPending
            || closeRequested || closePromptOpen || exitApproved || projectLoading != null || IsActiveProjectSwitching) return;
        foreach (var transition in pendingPlatformViewRestores.ToArray())
            Execute(() => { transition.RestoreViews(); pendingPlatformViewRestores.Remove(transition); });
    }
}
