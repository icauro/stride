using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly Dictionary<Guid, IWorkspaceRuntimeHistory> emptyWorkspaceTransactions = [];
    private readonly List<EmptyWorkspaceTransition> pendingEmptyViewRestores = [];
    private EmptyWorkspaceReplay? emptyWorkspaceReplay;
    private Guid? emptyHistoryNavigationTarget;
    private interface IWorkspaceRuntimeHistory { Task RetireViews(); }
    private sealed record EmptyWorkspaceReplay(EditorSessionHistory History, ProjectWorkspace Workspace, Guid Transaction,
        bool Redo, IWorkspaceRuntimeHistory Transition, Task Barrier);
    private sealed class EmptyWorkspaceTransition(EditorGame game, ProjectWorkspace workspace) : IWorkspaceRuntimeHistory
    {
        internal readonly ProjectWorkspace Workspace = workspace;
        internal readonly ProjectWorkspace.Activation Before = workspace.CaptureActivation();
        internal readonly CompiledProjectContent? Content = game.projectContent;
        internal GraphicsRecoveryState? Views;
        internal Task? Barrier;
        internal bool Applied;
        private CompiledProjectContent? animationContent;
        internal bool Current(bool empty) => ReferenceEquals(game.workspace, Workspace) && (empty
            ? Workspace.Package == null && Workspace.Session.CurrentProject == null && Workspace.CompiledOutput.Length == 0 && game.projectContent == null
            : Workspace.CaptureActivation() == Before && ReferenceEquals(game.projectContent, Content));
        Task IWorkspaceRuntimeHistory.RetireViews() => RetireViews();
        internal Task RetireViews()
        {
            if (!Current(Applied)) throw new InvalidOperationException("The active workspace changed before resource retirement.");
            if (Barrier is { IsCompleted: false }) return Barrier;
            if (!Applied)
            {
                var captured = game.CaptureWorkspaceTransitionViews(Workspace);
                game.ForgetWorkspaceTransitionViews(Views);
                Views = captured;
                animationContent = Views.AnimationBundle;
            }
            game.ClearSelectedAssetPreview();
            Barrier = game.BeginEditorGpuRetirementAsync(retireScenes: true, retireFonts: true); return Barrier;
        }
        internal void Change(bool empty)
        {
            if (Applied == empty) return;
            if (!Current(!empty)) throw new InvalidOperationException("The active workspace changed before native history replay.");
            if (Barrier?.IsCompletedSuccessfully != true) throw new InvalidOperationException("Native view retirement has not completed.");
            Barrier.GetAwaiter().GetResult(); game.ReleaseWorkspaceTransitionNativeViews(Views);
            if (empty)
            {
                if (Content != null && !game.retainedActiveProjectContent.Contains(Content)) game.retainedActiveProjectContent.Add(Content);
                Workspace.ClearActivation(); game.projectContent = null; game.selectedAssetPath = game.assetPreviewPath = null;
            }
            else
            {
                Workspace.RestoreActivation(Before); game.projectContent = Content;
                if (Content != null) game.retainedActiveProjectContent.Remove(Content);
                if (!game.pendingEmptyViewRestores.Contains(this)) game.pendingEmptyViewRestores.Add(this);
            }
            Applied = empty;
        }
        internal void RestoreViews()
        {
            if (Applied || !Current(false) || Views is not { } state) return;
            game.RebindRetiredFontSources();
            if (Content != null && !game.RestoreSceneDocumentGraphicsRecovery(state, Content)
                && state.PresentedDocument is { } legacyDocument
                && Workspace.AssetItems.Any(item => item.Id == legacyDocument.Asset.Id)
                && game.sessionHistory?.Registry.Find(legacyDocument.Path) is { IsPublished: true, IsDisposed: false } owner
                && ReferenceEquals(owner.Document, legacyDocument))
                game.RestoreGraphicsRecoveryScene(state);
            state.PreviewCreated = false; state.AnimationBundle = animationContent; state.OwnsAnimationBundle = false;
            game.RestoreGraphicsRecoveryPreview(state);
        }
    }
    private sealed class EmptyWorkspaceActivationOperation(EmptyWorkspaceTransition transition) : Operation
    {
        protected override void Rollback() => transition.Change(false);
        protected override void Rollforward() => transition.Change(true);
    }
    private bool TryPrepareEmptyWorkspaceHistoryReplay(bool redo)
    {
        if (sessionHistory == null || workspace == null) return true;
        var id = (redo ? sessionHistory.Stack.NextRedoTransaction : sessionHistory.Stack.NextUndoTransaction)?.Id;
        if (id == null || !emptyWorkspaceTransactions.TryGetValue(id.Value, out var transition)) return true;
        if (emptyWorkspaceReplay is { } waiting)
            return waiting.Transaction == id && ReferenceEquals(waiting.History, sessionHistory) && waiting.Barrier.IsCompletedSuccessfully;
        var barrier = transition.RetireViews();
        emptyWorkspaceReplay = new(sessionHistory, workspace, id.Value, redo, transition, barrier); return false;
    }
    private void PumpEmptyWorkspaceRuntime()
    {
        if (emptyWorkspaceReplay is { } replay && replay.Barrier.IsCompleted)
        {
            var next = (replay.Redo ? replay.History.Stack.NextRedoTransaction : replay.History.Stack.NextUndoTransaction)?.Id;
            if (!ReferenceEquals(sessionHistory, replay.History) || !ReferenceEquals(workspace, replay.Workspace) || next != replay.Transaction)
            { emptyWorkspaceReplay = null; emptyHistoryNavigationTarget = null; }
            else
            {
                Execute(() =>
                {
                    replay.Barrier.GetAwaiter().GetResult();
                    if (replay.Redo) RedoCurrentCommand(); else UndoCurrentCommand();
                    emptyWorkspaceReplay = null;
                    if (emptyHistoryNavigationTarget is { } target) { emptyHistoryNavigationTarget = null; NavigateSessionHistory(target); }
                });
            }
        }
    }
    private void PumpEmptyWorkspaceViewRestores()
    {
        foreach (var transition in pendingEmptyViewRestores.ToArray())
            Execute(() => { transition.RestoreViews(); pendingEmptyViewRestores.Remove(transition); });
    }
    private void ClearEmptyWorkspaceRuntimeState()
    { ClearWorkspaceTransitionViews(); emptyWorkspaceReplay = null; emptyHistoryNavigationTarget = null; pendingEmptyViewRestores.Clear(); pendingPlatformViewRestores.Clear(); emptyWorkspaceTransactions.Clear(); }
}
