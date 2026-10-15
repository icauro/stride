using Stride.Assets.Rendering;
using Stride.Core.Assets;
using Stride.Core.Serialization;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record SceneCompositorTarget(SceneDocumentView? View, EngineViewport Viewport,
        EntityHierarchyDocument Presented, SceneSynchronizationState State, CompiledProjectContent Content,
        SceneRuntimeSynchronizationState Runtime, int Width, int Height);
    private sealed record SceneCompositorReloadScope(ProjectWorkspace Workspace, EditorSessionHistory History,
        SolutionProject? Project, CompiledProjectContent Content, EditorAuthoringState CompositorOwner,
        AssetId Compositor, EditorAuthoringState? SettingsOwner, long SettingsRevision, SceneCompositorTarget[] Targets);
    private (SceneCompositorReloadScope Scope, string Revision)? sceneCompositorReloadAttempt;
    private string? sceneCompositorSettingsFailure;
    private void ReportSceneCompositorSourceFailure(AssetId? id, Exception error)
    {
        EditorGraphicsFailures.ThrowIfDeviceLost(GraphicsDevice, error);
        var detail = error.ToString();
        if (id is { } actual)
        {
            if (compositorCaptureErrors.GetValueOrDefault(actual) == detail) return;
            compositorCaptureErrors[actual] = detail; RecordAssetPreviewBuildFailure(actual, error, AssetThumbnailFailure.Broken);
        }
        else if (sceneCompositorSettingsFailure != detail)
        { sceneCompositorSettingsFailure = detail; RecordAssetPreviewRenderFailure("Scene settings", error); }
    }

    private bool IsCurrentSceneCompositorSettings(SceneCompositorReloadScope scope)
    {
        if (!ReferenceEquals(workspace, scope.Workspace) || !ReferenceEquals(sessionHistory, scope.History)
            || !ReferenceEquals(workspace.Session.CurrentProject, scope.Project) || !ReferenceEquals(projectContent, scope.Content)
            || !ReferenceEquals(authoring?.Find(scope.Compositor), scope.CompositorOwner)
            || !scope.CompositorOwner.IsPublished || scope.CompositorOwner.IsDisposed
            || closeRequested || closePromptOpen || closingDocument != null || IsPackagePlatformUpdating || IsActiveProjectSwitching
            || IsEditorGpuRetirementPending || IsEditorGraphicsRecovering) return false;
        try
        {
            var settings = EditorSceneSettings.ResolveCurrentGameSettings(workspace, sessionHistory, out var owner);
            return AttachedReferenceManager.GetAttachedReference(settings.GraphicsCompositor)?.Id == scope.Compositor
                && ReferenceEquals(owner, scope.SettingsOwner)
                && ((owner?.Document as IAssetTagDocument)?.EditRevision ?? -1) == scope.SettingsRevision;
        }
        catch (Exception error) { ReportSceneCompositorSourceFailure(scope.Compositor, error); return false; }
    }
    private bool IsCurrentSceneCompositorTarget(SceneCompositorTarget target)
    {
        if (!ReferenceEquals(target.Viewport.PresentedDocument, target.Presented)
            || !ReferenceEquals(target.Viewport.SourceContent, target.Content) || target.Viewport.IsSceneRetirementStarted
            || target.Viewport.RuntimeContentSynchronizationVersion != target.Runtime
            || (target.Viewport.Target?.Width ?? 0) != target.Width || (target.Viewport.Target?.Height ?? 0) != target.Height
            || !ReferenceEquals(authoring?.Find(target.Presented.Asset.Id)?.Document, target.Presented)
            || !(target.View != null ? sceneDocumentViews.TryGetValue(target.View.Entry.Id, out var view)
                && ReferenceEquals(view, target.View) && IsCurrentSceneDocumentView(view)
                : ReferenceEquals(engineViewport, target.Viewport))) return false;
        try { return target.Presented.SynchronizationState == target.State; }
        catch (Exception error) { ReportSceneCompositorSourceFailure(target.Presented.Asset.Id, error); return false; }
    }
    private void CaptureSceneCompositorTarget(List<SceneCompositorTarget> targets, SceneDocumentView? view,
        EngineViewport viewport, EntityHierarchyDocument presented, CompiledProjectContent content)
    {
        try { targets.Add(new(view, viewport, presented, presented.SynchronizationState, content,
            viewport.RuntimeContentSynchronizationVersion, viewport.Target?.Width ?? 0, viewport.Target?.Height ?? 0)); }
        catch (Exception error) { ReportSceneCompositorSourceFailure(presented.Asset.Id, error); }
    }

    private bool IsCurrentSceneCompositorReload(SceneCompositorReloadScope scope)
        => IsCurrentSceneCompositorSettings(scope) && scope.Targets.Any(IsCurrentSceneCompositorTarget);

    private bool RequiresSceneCompositorReload(SceneCompositorReloadScope scope)
        => scope.Targets.Any(target => target.Viewport.LoadedCompositorId != scope.Compositor
            || compositorContentOwners.TryGetValue(scope.Compositor, out var installed)
                && !ReferenceEquals(target.Content, installed.Bundle));

    private AssetMetadataDocument? PrepareSceneCompositorReload(out SceneCompositorReloadScope? scope)
    {
        scope = null;
        if (workspace == null || sessionHistory == null || projectContent == null || !CanStartAuthoringPreviewUpdate) return null;
        AssetId? id = null;
        try
        {
            var settings = EditorSceneSettings.ResolveCurrentGameSettings(workspace, sessionHistory, out var settingsOwner);
            id = AttachedReferenceManager.GetAttachedReference(settings.GraphicsCompositor)?.Id;
            // Native false-for-same preserves the existing renderer when no source resolves.
            if (id == null) { sceneCompositorReloadAttempt = null; return null; }
            var targets = new List<SceneCompositorTarget>();
            foreach (var view in sceneDocumentViews.Values)
                if (IsCurrentSceneDocumentView(view) && !view.Viewport.IsSceneRetirementStarted)
                    CaptureSceneCompositorTarget(targets, view, view.Viewport, view.Document, view.Content);
            if (engineViewport?.PresentedDocument is { } presented && engineViewport.SourceContent is { } content
                && !sceneDocumentViews.Values.Any(view => ReferenceEquals(view.Viewport, engineViewport)))
                CaptureSceneCompositorTarget(targets, null, engineViewport, presented, content);
            if (targets.Count == 0) { sceneCompositorReloadAttempt = null; return null; }
            if (workspace.Session.FindAsset(id.Value)?.Asset is not GraphicsCompositorAsset)
                throw new InvalidOperationException("The game settings reference is not a graphics compositor asset.");
            var document = sessionHistory.AssetGraphs.Resolve?.Invoke(id.Value) as AssetMetadataDocument;
            if (document?.CompositorAsset == null || authoring?.Find(id.Value) is not { IsPublished: true } owner
                || !ReferenceEquals(owner.Document, document))
                throw new InvalidOperationException("The graphics compositor has no current canonical owner.");
            scope = new(workspace, sessionHistory, workspace.Session.CurrentProject, projectContent, owner, id.Value,
                settingsOwner, (settingsOwner?.Document as IAssetTagDocument)?.EditRevision ?? -1, targets.ToArray());
            if (!IsCurrentSceneCompositorReload(scope)) { scope = null; return null; }
            sceneCompositorSettingsFailure = null;
            return document;
        }
        catch (Exception error)
        {
            ReportSceneCompositorSourceFailure(id, error);
            return null;
        }
    }

    private bool IsSameSceneCompositorReloadAttempt(SceneCompositorReloadScope scope, string revision)
    {
        if (sceneCompositorReloadAttempt is not { } previous || previous.Revision != revision) return false;
        var old = previous.Scope;
        return ReferenceEquals(old.Workspace, scope.Workspace) && ReferenceEquals(old.History, scope.History)
            && ReferenceEquals(old.Project, scope.Project) && ReferenceEquals(old.Content, scope.Content)
            && ReferenceEquals(old.CompositorOwner, scope.CompositorOwner) && old.Compositor == scope.Compositor
            && ReferenceEquals(old.SettingsOwner, scope.SettingsOwner) && old.SettingsRevision == scope.SettingsRevision
            && old.Targets.SequenceEqual(scope.Targets);
    }
    private void RememberSceneCompositorReloadAttempt(SceneCompositorReloadScope scope, string revision)
        => sceneCompositorReloadAttempt = (scope, revision);
    private void ForgetSceneCompositorView(EngineViewport viewport)
    {
        if (sceneCompositorReloadAttempt is { } attempt && attempt.Scope.Targets.Any(target => ReferenceEquals(target.Viewport, viewport)))
            sceneCompositorReloadAttempt = null;
    }
}
