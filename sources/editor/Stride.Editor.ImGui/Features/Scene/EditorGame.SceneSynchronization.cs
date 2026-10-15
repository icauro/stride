using Stride.Core.Mathematics;
using Stride.Games;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    // Close/recovery compatibility identities; actual failures belong to each native view.
    private EntityHierarchyDocument? failedSceneDocument;
    private object? failedRuntimeScene;
    private readonly Dictionary<EngineViewport, SceneViewFailures> sceneViewFailures = new(ReferenceEqualityComparer.Instance);
    private sealed class SceneViewFailures { internal SceneFailure? Synchronize, Update, Render; }
    private sealed record SceneFailure(SceneViewCertificate Certificate, string Type, string Message);
    private readonly record struct SceneViewCertificate(EntityHierarchyDocument? Source, object? Runtime,
        long Structure, SceneSynchronizationState Authored, SceneSynchronizationState Presented, long Children,
        long Settings, long Provider, long Selection, SceneRuntimeSynchronizationState RuntimeContent, object? Content, object? Target,
        Matrix CameraView, float Fov, float Near, float Far, float OrthoSize, bool Ortho, string? Stream,
        Stride.Assets.Presentation.AssetEditors.GameEditor.RenderMode Mode, bool SelectionVisible);

    private SceneViewCertificate CaptureSceneViewCertificate(EntityHierarchyDocument source, EngineViewport viewport,
        long settings, bool rendering = false)
        => new(source, viewport.RuntimeScene, source.StructureVersion, source.SynchronizationState,
            (viewport.PresentedDocument ?? source).SynchronizationState, viewport.ChildSceneStateVersion, settings,
            source.SessionHistory?.Extensions?.Version ?? 0, viewport.SelectionSourceVersion,
            viewport.RuntimeContentSynchronizationVersion, viewport.SourceContent, rendering ? viewport.Target : null,
            rendering ? viewport.Camera.Component.ViewMatrix : default,
            rendering ? viewport.Camera.Component.VerticalFieldOfView : 0, rendering ? viewport.Camera.Component.NearClipPlane : 0,
            rendering ? viewport.Camera.Component.FarClipPlane : 0, rendering ? viewport.Camera.OrthographicSize : 0,
            rendering && viewport.Camera.Orthographic, viewport.ActiveMaterialStream, viewport.NativeRenderMode, viewport.ShowSelection);

    private void ClearSceneSynchronizationFailure()
    { sceneViewFailures.Clear(); failedSceneDocument = null; failedRuntimeScene = null; }
    private void ForgetSceneViewSynchronization(EngineViewport viewport)
    {
        sceneViewFailures.Remove(viewport);
        if (ReferenceEquals(failedRuntimeScene, viewport.RuntimeScene))
        { failedSceneDocument = null; failedRuntimeScene = null; }
    }

    private bool RunSceneViewPhase(EntityHierarchyDocument source, EngineViewport viewport, int phase, GameTime? time = null, bool activateMissingRoot = false)
    {
        if (viewport.IsSceneRetirementStarted || IsEditorGraphicsRecovering) return false;
        if (!sceneViewFailures.TryGetValue(viewport, out var failures))
            sceneViewFailures.Add(viewport, failures = new());
        var previous = phase == 0 ? failures.Synchronize : phase == 1 ? failures.Update : failures.Render;
        var settings = -1L;
        SceneViewCertificate certificate = default;
        try
        {
            settings = viewport.SceneSettingsVersion;
            certificate = CaptureSceneViewCertificate(source, viewport, settings, phase == 2);
            if (previous?.Certificate == certificate) return false;
            if (phase == 0)
            {
                if (!ReferenceEquals(viewport, engineViewport))
                { viewport.Gizmos?.Hide(); viewport.Picker?.Cancel("Scene command owner is inactive"); }
                viewport.Synchronize(source);
                if (activateMissingRoot && source is SceneDocument active && !viewport.IsPresentedScene(active)
                    && !IsCurrentPresentedSceneRoot(active) && viewport.PresentedDocument is SceneDocument root)
                    ActivateSceneRoot(root);
            }
            else if (phase == 1) viewport.Update(time!);
            else viewport.Render(time!);
            if (phase == 0) failures.Synchronize = null;
            else if (phase == 1) failures.Update = null;
            else failures.Render = null;
            if (phase == 2 && failures.Synchronize == null && failures.Update == null) viewport.SetRuntimeSourceFailed(false);
            if (ReferenceEquals(viewport, engineViewport) && failures.Synchronize == null && failures.Update == null && failures.Render == null)
            { failedSceneDocument = null; failedRuntimeScene = null; }
            return true;
        }
        catch (Exception error)
        {
            EditorGraphicsFailures.ThrowIfDeviceLost(GraphicsDevice, error);
            viewport.SetRuntimeSourceFailed(true);
            // Never resolve the failed settings provider again in its exception path.
            certificate = certificate with { Runtime = viewport.RuntimeScene,
                RuntimeContent = viewport.RuntimeContentSynchronizationVersion,
                Children = viewport.CachedChildSceneStateVersion };
            var failure = new SceneFailure(certificate, error.GetType().FullName ?? error.GetType().Name, error.Message);
            if (phase == 0) failures.Synchronize = failure;
            else if (phase == 1) failures.Update = failure;
            else failures.Render = failure;
            if (ReferenceEquals(viewport, engineViewport))
            { failedSceneDocument = source; failedRuntimeScene = viewport.RuntimeScene; }
            statusMessage = error.Message;
            if (previous != failure)
                outputLog.Append("Project", Stride.Core.Diagnostics.LogMessageType.Error, "Scene", error.ToString());
            return false;
        }
    }

    private bool SynchronizeSceneView(EntityHierarchyDocument source, EngineViewport viewport)
        => RunSceneViewPhase(source, viewport, 0);
    private void SynchronizeSceneDocument()
    {
        if (document == null || engineViewport == null || projectContent == null || engineViewport.PresentedDocument == null) return;
        RunSceneViewPhase(document, engineViewport, 0, activateMissingRoot: true);
    }
    private bool IsSceneDocumentViewLoaded(EngineViewport viewport, SceneDocument scene)
        => sceneLoadedOverrides.TryGetValue(scene.Asset.Id, out var loaded) ? loaded
            : ReferenceEquals(scene, viewport.PresentedDocument) || IsSceneLoaded(scene);

    private void SynchronizeAndUpdateSceneDocumentView(SceneDocumentView view, GameTime time)
    {
        var active = ReferenceEquals(engineViewport, view.Viewport);
        var source = active ? document ?? view.Document : view.Document;
        view.LoadedPolicy ??= scene => IsSceneDocumentViewLoaded(view.Viewport, scene);
        view.Viewport.SceneLoaded = view.LoadedPolicy;
        if (SynchronizeSceneView(source, view.Viewport))
            RunSceneViewPhase(source, view.Viewport, 1, time);
    }
    private void RenderSceneDocumentView(SceneDocumentView view, GameTime time)
    {
        var source = ReferenceEquals(engineViewport, view.Viewport) ? document ?? view.Document : view.Document;
        if (sceneViewFailures.TryGetValue(view.Viewport, out var failures) && (failures.Synchronize != null || failures.Update != null)) return;
        RunSceneViewPhase(source, view.Viewport, 2, time);
    }
}
