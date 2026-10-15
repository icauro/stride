namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class SceneDocumentRecovery
    {
        internal required EditorDocumentEntry Entry;
        internal required EntityHierarchyDocument Document;
        internal required EditorAuthoringState State;
        internal required ProjectWorkspace Workspace;
        internal required CompiledProjectContent ProjectContent, Content;
        internal required EngineViewportCompiledInputs Inputs;
        internal required Action<SceneDocumentView> Restore;
        internal required int Width, Height;
        internal bool Visible, ActiveAlias, Ready, Restored;
        internal bool Restorable = true;
        internal IDisposable? DocumentLease, ProjectLease, ContentLease;
        internal void Release()
        {
            if (ContentLease != null) { ContentLease.Dispose(); ContentLease = null; }
            if (ProjectLease != null) { ProjectLease.Dispose(); ProjectLease = null; }
            if (DocumentLease != null) { DocumentLease.Dispose(); DocumentLease = null; }
        }
    }
    private readonly Dictionary<GraphicsRecoveryState, List<SceneDocumentRecovery>> sceneDocumentRecovery = [];
    private readonly Dictionary<GraphicsRecoveryState, HashSet<SceneDocumentView>> staleSceneRecoveryBorrowers = [];

    private void CaptureSceneDocumentGraphicsRecovery(GraphicsRecoveryState recovery)
    {
        if (!ReferenceEquals(recovery.Workspace, workspace) || !ReferenceEquals(recovery.History, sessionHistory))
            throw new InvalidOperationException("The recovery scene session changed before capture.");
        if (!sceneDocumentRecovery.TryGetValue(recovery, out var snapshots)) sceneDocumentRecovery.Add(recovery, snapshots = []);
        CaptureSceneDocumentControlState();
        foreach (var view in sceneDocumentViews.Values)
        {
            if (!IsCurrentSceneDocumentView(view))
            {
                // The native authority still owns its actual graph/content/import
                // leases. Keep that owner through CPU/GPU retirement, without
                // acquiring a new lease from a replaced canonical state.
                if (!staleSceneRecoveryBorrowers.TryGetValue(recovery, out var borrowers))
                    staleSceneRecoveryBorrowers.Add(recovery, borrowers = []);
                borrowers.Add(view);
                foreach (var old in snapshots.Where(old => ReferenceEquals(old.Entry, view.Entry)
                    && ReferenceEquals(old.Document, view.Document) && ReferenceEquals(old.State, view.State)))
                    old.Restorable = false;
                continue;
            }
            var snapshot = snapshots.FirstOrDefault(snapshot => snapshot.Restorable && ReferenceEquals(snapshot.Entry, view.Entry)
                && ReferenceEquals(snapshot.Document, view.Document) && ReferenceEquals(snapshot.State, view.State));
            if (snapshot == null)
            {
                snapshot = new SceneDocumentRecovery
                {
                    Entry = view.Entry, Document = view.Document, State = view.State, Workspace = view.Workspace,
                    ProjectContent = view.ProjectContent, Content = view.Content, Restore = CaptureSceneDocumentRecoveryControls(view),
                    Inputs = view.Viewport.CompiledInputs ?? throw new InvalidOperationException("The actual compiled scene/compositor identities are unavailable."),
                    Width = view.Viewport.Target?.Width ?? 0, Height = view.Viewport.Target?.Height ?? 0,
                    Visible = view.Viewport.Visible, ActiveAlias = ReferenceEquals(engineViewport, view.Viewport),
                };
                snapshots.Add(snapshot); // Retain partial lease acquisition for phase-specific retry/cleanup.
            }
            snapshot.DocumentLease ??= snapshot.State.AcquireView();
            if (ReferenceEquals(snapshot.ProjectContent, snapshot.Content)) snapshot.ProjectLease ??= view.Viewport.AcquireSceneContentRecoveryLease();
            else
            {
                snapshot.ProjectLease ??= snapshot.ProjectContent.AcquireLease();
                snapshot.ContentLease ??= view.Viewport.AcquireSceneContentRecoveryLease();
            }
            snapshot.Ready = true;
        }
    }

    private Action<SceneDocumentView> CaptureSceneDocumentRecoveryControls(SceneDocumentView view)
    {
        var viewport = view.Viewport;
        var camera = viewport.Camera.Capture(viewport.ShowSelection) with
        { Grid = viewport.Grid?.Settings, CameraPreview = viewport.ShowCameraPreview };
        var transform = viewport.Gizmos?.CaptureSettings(); var mode = viewport.Gizmos?.Mode; var space = viewport.Gizmos?.Space;
        var components = viewport.ComponentGizmos?.CaptureSettings(); var navigation = viewport.CaptureVisibleNavigationGroups();
        var materialStream = viewport.ActiveMaterialStream; var gamePreview = viewport.IsGamePreview;
        var probes = viewport.LightProbeVolumes?.Visible ?? false;
        var materialSelection = viewport.MaterialSelectionEnabled;
        var context = GetSceneToolbarContext(view);
        var picking = context.Picking; var bounces = context.LightProbeBounces; var lensError = context.LensError;
        var pendingGrid = context.PendingGridSave;
        return restored =>
        {
            var target = restored.Viewport;
            target.Camera.Restore(camera); target.ShowSelection = camera.Selection; target.ShowCameraPreview = camera.CameraPreview;
            target.Grid?.ApplySettings(camera.Grid ?? SceneGridSettings.Default);
            if (transform != null && target.Gizmos is { } gizmos)
            { gizmos.RestoreSettings(transform); if (mode is { } selectedMode) gizmos.Mode = selectedMode; if (space is { } selectedSpace) gizmos.Space = selectedSpace; }
            if (components != null) target.ComponentGizmos?.RestoreSettings(components);
            target.RestoreVisibleNavigationGroups(navigation);
            target.MaterialSelectionEnabled = materialSelection;
            if (target.LightProbeVolumes != null) target.LightProbeVolumes.Visible = probes;
            target.RestoreSceneRenderMode(materialStream);
            if (gamePreview && target.AvailableSceneRenderModes.FirstOrDefault(candidate => candidate.PreviewGameGraphicsCompositor) is { } preview)
                target.SetSceneRenderMode(preview);
            var controls = GetSceneToolbarContext(restored);
            controls.Picking = picking; controls.LightProbeBounces = bounces; controls.LensError = lensError; controls.PendingGridSave = pendingGrid;
            target.SetPickingTypeFilter(picking.Predicate, picking.Version);
        };
    }

    private void ReleaseSceneDocumentGraphicsForRecovery(GraphicsRecoveryState recovery)
    {
        EnsureSceneCpuRetirementCompleted(); EnsureEditorGpuRetirementCompleted();
        if (!sceneDocumentRecovery.TryGetValue(recovery, out var snapshots) || snapshots.Any(snapshot => snapshot.Restorable && !snapshot.Ready))
            throw new InvalidOperationException("Every scene recovery snapshot must finish before graphics release.");
        // The retained snapshots already capture and restore the actual controls.
        // Recovery release must not author package settings or add undo operations.
        ClearSceneDocumentViews(persistViewSettings: false); // Frontend membership and canonical document owners remain unchanged.
    }

    private bool KeepSceneDocumentGraphicsRecoveryBundle(CompiledProjectContent bundle, GraphicsRecoveryState recovery)
        => sceneDocumentViews.Values.Any(view => ReferenceEquals(view.Content, bundle) || ReferenceEquals(view.ProjectContent, bundle))
            || sceneDocumentRecovery.TryGetValue(recovery, out var snapshots)
                && snapshots.Any(snapshot => ReferenceEquals(snapshot.Content, bundle) || ReferenceEquals(snapshot.ProjectContent, bundle))
            || staleSceneRecoveryBorrowers.TryGetValue(recovery, out var borrowers)
                && borrowers.Any(view => !view.Viewport.IsResourceRetirementComplete
                    && (ReferenceEquals(view.Content, bundle) || ReferenceEquals(view.ProjectContent, bundle)));

    private bool RestoreSceneDocumentGraphicsRecovery(GraphicsRecoveryState recovery, CompiledProjectContent? transitionContent = null)
    {
        if (!sceneDocumentRecovery.TryGetValue(recovery, out var snapshots)) return false;
        var hasStaleBorrowers = staleSceneRecoveryBorrowers.TryGetValue(recovery, out var stale) && stale.Count != 0;
        var needsCurrentOwners = hasStaleBorrowers;
        if (snapshots.Count == 0 && !hasStaleBorrowers) return false;
        if (!ReferenceEquals(recovery.Workspace, workspace) || !ReferenceEquals(recovery.History, sessionHistory) || renderer == null)
            throw new InvalidOperationException("The recovery scene session changed before restoration.");
        foreach (var snapshot in snapshots)
        {
            if (!snapshot.Restorable || !IsCurrentDocumentPresentation(snapshot.Entry)
                || !ReferenceEquals(snapshot.Entry.Document, snapshot.Document) || !ReferenceEquals(snapshot.Entry.State, snapshot.State))
            { snapshot.Restorable = false; needsCurrentOwners = true; continue; }
            // Membership replay may remove an editor's real owner. Never reopen
            // that owner, or replace it with a document bearing the same path.
            if (transitionContent != null && (!documents.Entries.Contains(snapshot.Entry)
                || !snapshot.Workspace.AssetItems.Any(item => item.Id == snapshot.Document.Asset.Id))) continue;
            var baseContent = transitionContent ?? snapshot.ProjectContent;
            var sameGeneration = ReferenceEquals(baseContent, snapshot.ProjectContent);
            var sceneContent = sameGeneration ? snapshot.Content : baseContent;
            if (!snapshot.Ready || !IsCurrentDocumentPresentation(snapshot.Entry) || !ReferenceEquals(snapshot.Entry.Document, snapshot.Document)
                || !ReferenceEquals(snapshot.Entry.State, snapshot.State)
                || !(transitionContent != null ? ReferenceEquals(projectContent, baseContent) : IsCurrentOrRetainedSceneProjectContent(baseContent)))
                throw new InvalidOperationException("A captured recovery scene owner or base generation changed.");
            if (!sceneDocumentViews.TryGetValue(snapshot.Entry.Id, out var next))
            {
                var viewport = new EngineViewport(this, renderer)
                {
                    ResolveChildSceneDocument = ResolveSceneHierarchyDocument, ResolveSceneWorldOffset = GetSceneHierarchyOffset,
                    SceneLoaded = IsSceneLoaded, SceneLocked = IsSceneLocked, EntityLocked = IsHierarchyEntityLocked,
                    ScenePresentationVersion = () => scenePresentationVersion,
                };
                next = new SceneDocumentView { Entry = snapshot.Entry, Document = snapshot.Document, State = snapshot.State,
                    Workspace = snapshot.Workspace, ProjectContent = baseContent, Content = sceneContent, Viewport = viewport };
                next.LoadedPolicy = candidate => IsSceneDocumentViewLoaded(viewport, candidate);
                viewport.SceneLoaded = next.LoadedPolicy;
                try
                {
                    if (sameGeneration)
                        viewport.Load(snapshot.Document, snapshot.Workspace, sceneContent, snapshot.ContentLease ?? snapshot.ProjectLease, snapshot.Inputs);
                    else
                        viewport.Load(snapshot.Document, snapshot.Workspace, sceneContent);
                    if (snapshot.Width > 0 && snapshot.Height > 0) viewport.Resize(snapshot.Width, snapshot.Height);
                    ReplaySceneDocumentResources(next);
                    if (!IsCurrentSceneDocumentView(next)) throw new InvalidOperationException("The restored scene owner changed before publication.");
                    sceneDocumentViews.Add(snapshot.Entry.Id, next);
                }
                catch
                {
                    drainingSceneDocumentViews.Add(next); retiredSceneDocumentBorrowers.Add(next); RetireEditorPreview(next);
                    viewport.BeginSceneRetirementDrain(); throw;
                }
            }
            if (!IsCurrentSceneDocumentView(next) || !ReferenceEquals(next.Content, sceneContent))
                throw new InvalidOperationException("An unrelated scene view occupies the recovery entry.");
            snapshot.Restore(next); next.Viewport.Synchronize(snapshot.Document); snapshot.Restored = true;
            next.Viewport.Visible = snapshot.Visible;
            if (snapshot.ActiveAlias) engineViewport = next.Viewport;
        }
        if (needsCurrentOwners && projectContent != null)
        {
            // Rebuild the real current canonical owner from the actual current
            // compiled workspace, never from a stale view's camera/graph/bundle.
            foreach (var entry in documents.Entries)
                if (entry.Document is EntityHierarchyDocument current && IsCurrentDocumentPresentation(entry)
                    && workspace!.AssetItems.Any(item => item.Id == current.Asset.Id)
                    && !sceneDocumentViews.ContainsKey(entry.Id))
                {
                    var certificate = (entry.Document, entry.State, projectContent, current.SynchronizationState);
                    if (failedSceneViewPreparation.TryGetValue(entry.Id, out var previous) && previous == certificate) continue;
                    try { EnsureSceneDocumentView(entry); failedSceneViewPreparation.Remove(entry.Id); }
                    catch (Exception error)
                    {
                        EditorGraphicsFailures.ThrowIfDeviceLost(GraphicsDevice, error);
                        failedSceneViewPreparation[entry.Id] = certificate;
                        statusMessage = error.Message;
                        outputLog.Append("Project", Stride.Core.Diagnostics.LogMessageType.Error, "Scene", error.ToString());
                    }
                }
            if (documents.Active is { } activeEntry && ReferenceEquals(activeEntry.Document, document)
                && sceneDocumentViews.TryGetValue(activeEntry.Id, out var activeView) && IsCurrentSceneDocumentView(activeView))
                engineViewport = activeView.Viewport;
        }
        if (documents.Active is { } active && ReferenceEquals(engineViewport?.PresentedDocument, active.Document))
            RestoreSceneDocumentControlState(active);
        return true;
    }

    private void ClearSceneDocumentGraphicsRecovery(GraphicsRecoveryState recovery)
    {
        if (!sceneDocumentRecovery.TryGetValue(recovery, out var snapshots)) return;
        while (snapshots.Count != 0) { snapshots[0].Release(); snapshots.RemoveAt(0); }
        sceneDocumentRecovery.Remove(recovery);
        staleSceneRecoveryBorrowers.Remove(recovery);
    }
}
