namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool RecreateSceneCompositorViewports(SceneCompositorReloadScope scope, CompiledProjectContent compiled, bool onlyMismatched = false)
    {
        var succeeded = true;
        foreach (var target in scope.Targets)
        {
            if (!IsCurrentSceneCompositorSettings(scope)) return false;
            if (!IsCurrentSceneCompositorTarget(target)) continue;
            if (onlyMismatched && target.Viewport.LoadedCompositorId == scope.Compositor
                && ReferenceEquals(target.Content, compiled)) continue;
            try { RecreateSceneCompositorViewport(scope, target, compiled); }
            catch (Exception error)
            {
                EditorGraphicsFailures.ThrowIfDeviceLost(GraphicsDevice, error);
                // A failed native replacement retains that exact previous authority.
                // Other canonical views can adopt the same real compiled generation.
                RecordAssetPreviewBuildFailure(scope.Compositor, error, AssetThumbnailFailure.Broken);
                succeeded = false;
            }
        }
        return succeeded;
    }

    private void RecreateSceneCompositorViewport(SceneCompositorReloadScope scope, SceneCompositorTarget target, CompiledProjectContent compiled)
    {
        if (renderer == null || !IsCurrentSceneCompositorSettings(scope) || !IsCurrentSceneCompositorTarget(target)) return;
        var previous = target.Viewport;
        var active = ReferenceEquals(engineViewport, previous);
        var source = active ? document ?? target.Presented : target.Presented;
        var camera = previous.Camera.Capture(previous.ShowSelection) with
            { Grid = previous.Grid?.Settings, CameraPreview = previous.ShowCameraPreview };
        var transform = previous.Gizmos?.CaptureSettings();
        var transformMode = previous.Gizmos?.Mode;
        var transformSpace = previous.Gizmos?.Space;
        var components = previous.ComponentGizmos?.CaptureSettings();
        var navigation = previous.CaptureVisibleNavigationGroups();
        var stream = previous.ActiveMaterialStream;
        var nativeMode = previous.NativeRenderMode;
        var visible = previous.Visible;
        var next = new EngineViewport(this, renderer)
        {
            ResolveChildSceneDocument = ResolveSceneHierarchyDocument,
            ResolveSceneWorldOffset = GetSceneHierarchyOffset,
            SceneLocked = IsSceneLocked, EntityLocked = IsHierarchyEntityLocked,
            ScenePresentationVersion = () => scenePresentationVersion,
        };
        next.SceneLoaded = scene => IsSceneDocumentViewLoaded(next, scene);
        SceneDocumentView? prepared = null;
        try
        {
            next.Load(target.Presented, scope.Workspace, compiled);
            if (previous.Target is { } framebuffer) next.Resize(framebuffer.Width, framebuffer.Height);
            if (target.View != null)
            {
                prepared = new SceneDocumentView { Entry = target.View.Entry, Document = target.View.Document,
                    State = target.View.State, Workspace = target.View.Workspace, ProjectContent = target.View.ProjectContent,
                    Content = compiled, Viewport = next };
                ReplaySceneDocumentResources(prepared);
            }
            else ReplayPreparedSceneViewportResources(next, target.Presented);
            next.Camera.Restore(camera); next.ShowSelection = previous.ShowSelection;
            next.MaterialSelectionEnabled = previous.MaterialSelectionEnabled;
            next.ShowCameraPreview = camera.CameraPreview; next.Grid?.ApplySettings(camera.Grid ?? SceneGridSettings.Default);
            if (transform != null && next.Gizmos is { } gizmos)
            {
                gizmos.RestoreSettings(transform);
                if (transformMode is { } mode) gizmos.Mode = mode;
                if (transformSpace is { } space) gizmos.Space = space;
            }
            if (components != null) next.ComponentGizmos?.RestoreSettings(components);
            next.RestoreVisibleNavigationGroups(navigation);
            if (next.LightProbeVolumes != null) next.LightProbeVolumes.Visible = previous.LightProbeVolumes?.Visible == true;
            next.RestoreSceneRenderMode(stream);
            if (next.AvailableSceneRenderModes.FirstOrDefault(mode => mode.Mode == nativeMode
                && (string.IsNullOrEmpty(stream) || mode.StreamDescriptor?.Name == stream)) is { } renderMode)
                next.SetSceneRenderMode(renderMode);
            next.Synchronize(source); next.Visible = visible;
            if (!IsCurrentSceneCompositorSettings(scope) || !IsCurrentSceneCompositorTarget(target))
                throw new InvalidOperationException("The requesting scene view changed during compositor preparation.");
            if (active) BindActiveSceneCompositorGizmos(next);
            if (prepared != null)
                AdoptPreparedSceneDocumentView(target.View!, prepared);
            if (active)
            {
                engineViewport = next; configuredGizmos = next.Gizmos;
                if (prepared != null) RestoreSceneDocumentControlState(prepared.Entry);
                UpdatePickingOptions();
            }
            if (prepared == null)
            {
                var leases = importedContentOwners.Values.Concat(retiredImportContentOwners).Distinct()
                    .Where(owner => previous.UsesImportedContent(owner.Values.Select(value => value.Value).ToArray(), source))
                    .Select(owner => new ImportedContentLease(owner)).ToArray();
                RetireEditorPreview(new RetiredEngineViewport(previous, leases));
            }
            ForgetSceneViewSynchronization(previous);
            ForgetSceneCompositorView(previous);
        }
        catch
        {
            // No authored/global document pointers were changed while preparing.
            // Failed replacement owns its own bundle/import/canonical leases until native retirement.
            if (!ReferenceEquals(engineViewport, next)
                && !sceneDocumentViews.Values.Any(view => ReferenceEquals(view.Viewport, next)))
                RetireEditorPreview(next);
            throw;
        }
    }

    private void ReplayPreparedSceneViewportResources(EngineViewport viewport, EntityHierarchyDocument owner)
    {
        foreach (var resource in refreshedModels) viewport.ReplaceRuntimeModel(resource.Url, resource.Model);
        foreach (var resource in refreshedSceneTextures) viewport.ReplaceRuntimeTexture(resource.Key, resource.Value.Texture, owner);
        ReplayCompiledSpriteSheets(viewport, owner);
        foreach (var installed in installedSkyboxes.Values)
            if (TryGetRefreshedSkybox(installed.Url, out var skybox)) viewport.ReplaceRuntimeSkybox(installed.Url, skybox, owner);
        foreach (var imported in importedContentOwners.Values.OrderBy(value => value.Generation))
        {
            RetainSceneDocumentImportOwner(viewport, imported);
            viewport.ReplaceRuntimeImportedContent(imported.Values, owner);
        }
    }
    private void BindActiveSceneCompositorGizmos(EngineViewport viewport)
    {
        if (viewport.Gizmos is not { } gizmos) return;
        gizmos.ResolveRuntimeEntity = viewport.ResolveRuntimeEntity;
        gizmos.CanEditDocument = owner => viewport.RuntimeSourceAvailable && (owner is not SceneDocument scene
            || IsSceneDocumentViewLoaded(viewport, scene) && !IsSceneLocked(scene));
        gizmos.SelectionDocuments = () => SelectedHierarchyDocuments; gizmos.SelectionPrimary = () => CompositeHierarchyPrimary;
        gizmos.SelectionRoots = viewport.GetWorldTransformRoots; BindSceneRootTransformTargets(gizmos, viewport);
        gizmos.SelectionPolicyVersion = () => scenePresentationVersion;
        gizmos.CanDuplicateSelectedEntities = () => CanDuplicateSceneGizmoSelection;
        gizmos.DuplicateSelectedEntities = DuplicateSceneGizmoSelection;
    }

    private sealed class RetiredEngineViewport : IDisposable, IEditorResourceRetirementDrain, IRuntimeTextureConsumer
    {
        private readonly EngineViewport viewport;
        private bool viewportDisposed;
        internal RetiredEngineViewport(EngineViewport viewport, ImportedContentLease[] leases)
        {
            this.viewport = viewport;
            foreach (var lease in leases) viewport.RetainSceneLease(lease);
            _ = viewport.BeginSceneRetirementDrain();
        }
        public bool UsesRuntimeTexture(Stride.Graphics.Texture texture) => !viewportDisposed && viewport.UsesRuntimeTexture(texture);
        public bool IsRetirementDrainComplete => viewport.IsSceneRetirementDrainComplete;
        public bool PumpRetirementDrain() => viewport.PumpSceneRetirementDrain();
        public void Dispose()
        { if (!viewportDisposed) { viewport.Dispose(); viewportDisposed = true; } }
    }
}
