using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record LightProbeRequest(EngineViewport Viewport, EditorSessionHistory History, ProjectWorkspace Workspace,
        EditorAuthoringState Owner, long Revision, int Bounces);
    private LightProbeRequest? pendingLightProbeRequest;
    private LightProbeRequest? activeLightProbeRequest;
    private bool IsSceneLightProbeBusy => pendingCubemapRequest != null || pendingLightProbeRequest != null || activeLightProbeRequest != null || engineViewport?.HasLightProbeBake == true;
    private int lightProbeBounces = 1;
    private void DrawSceneLightingControls(SceneToolbarContext? context = null)
    {
        if (context != null && !context.Current()) return;
        var engineViewport = context?.Viewport ?? this.engineViewport;
        if (engineViewport?.LightProbeVolumes is not { } volumes) return;
        if (SceneToolbarIcon("scene-toolbar-lighting", EditorLocalization.Text("Light probes and cubemaps...", "ToolTip"))) Gui.OpenPopup("Light probes");
        if (!Gui.BeginPopup("Light probes")) return;
        Gui.TextUnformatted(EditorLocalization.Text("Light probes"));
        Gui.SetNextItemWidth(UiScale(70));
        var bounces = context?.LightProbeBounces ?? lightProbeBounces;
        Gui.InputInt(EditorLocalization.Widget("Bounces:"), ref bounces); bounces = Math.Max(0, bounces);
        if (context == null) lightProbeBounces = bounces; else context.LightProbeBounces = bounces;
        Gui.BeginDisabled(context?.CanAuthor() == false || IsAuthoringPipelineUpdating || IsEditorGraphicsRecovering || pendingLightProbeRequest != null || engineViewport.PresentedDocument?.CanSave != true);
        if (icons!.TextButton("light-probes-compute", "Compute", EditorLocalization.Text("Compute lighting using light probes", "ToolTip"))) RunSceneToolbarAuthoring(context, () => QueueLightProbeCapture(bounces));
        Gui.SameLine(); if (icons.TextButton("light-probes-reset", EditorLocalization.Widget("Reset"), EditorLocalization.Text("Reset light probes", "ToolTip"))) RunSceneToolbarAuthoring(context, () => QueueLightProbeCapture(0));
        Gui.EndDisabled();
        Gui.Separator(); Gui.TextUnformatted(EditorLocalization.Text("Cubemap"));
        Gui.BeginDisabled(context?.CanAuthor() == false || IsAuthoringPipelineUpdating || IsEditorGraphicsRecovering);
        if (icons.TextButton("scene-cubemap-capture", EditorLocalization.Widget("Capture"), EditorLocalization.Text("Generate a cubemap from the scene view and save as a texture", "ToolTip"))) RunSceneToolbarAuthoring(context, () => Execute(QueueSceneCubemapCapture));
        Gui.EndDisabled();
        Gui.EndPopup();
    }
    private void QueueLightProbeCapture(int bounces)
    {
        var current = engineViewport?.PresentedDocument;
        if (engineViewport == null || current == null || workspace == null || SessionAuthoringHistory is not { } history
            || history.Registry.Find(current.Asset.Id) is not { IsPublished: true } owner || !ReferenceEquals(owner.Document, current)) return;
        pendingLightProbeRequest = new(engineViewport, history, workspace, owner, current.EditRevision, bounces);
    }
    private void PumpSceneLightProbeCommands()
    {
        PumpSceneCubemapCapture();
        var request = activeLightProbeRequest ?? pendingLightProbeRequest;
        if (request == null) return;
        if (IsAuthoringPipelineUpdatingExceptSceneLightProbe || IsEditorGraphicsRecovering || closeRequested || closePromptOpen || closingDocument != null || !request.Viewport.Visible
            || !ReferenceEquals(engineViewport, request.Viewport) || !ReferenceEquals(workspace, request.Workspace)
            || !ReferenceEquals(SessionAuthoringHistory, request.History) || !request.Owner.IsPublished || request.Owner.IsDisposed
            || request.Owner.Document is not EntityHierarchyDocument current || current.EditRevision != request.Revision
            || !ReferenceEquals(request.History.Registry.Find(current.Asset.Id), request.Owner))
        { CancelSceneLightProbeCommands(); return; }
        var visible = request.Viewport.Visible;
        try
        {
            request.Viewport.Visible = true;
            ExecuteSceneCapture(() =>
            {
                try
                {
                    if (activeLightProbeRequest == null)
                    {
                        request.Viewport.BeginLightProbeBake(request.Bounces);
                        activeLightProbeRequest = request with { Revision = current.EditRevision };
                        pendingLightProbeRequest = null;
                    }
                    if (request.Viewport.StepLightProbeBake()) activeLightProbeRequest = null;
                }
                catch (Exception failure)
                {
                    try { CancelSceneLightProbeCommands(); }
                    catch (Exception recovery) { throw new AggregateException("Light probe capture and cleanup failed.", failure, recovery); }
                    throw;
                }
            });
        }
        finally { request.Viewport.Visible = visible; }
    }
    private void CancelSceneLightProbeCommands()
    {
        pendingLightProbeRequest = null;
        pendingCubemapRequest = null;
        (activeLightProbeRequest?.Viewport ?? engineViewport)?.CancelLightProbeBake();
        activeLightProbeRequest = null;
    }
    private void ExecuteSceneCapture(Action action)
    {
        try { action(); }
        catch (Stride.Graphics.GraphicsDeviceException) { throw; }
        catch (Exception error)
        {
            if (error is AggregateException aggregate && aggregate.Flatten().InnerExceptions.OfType<Stride.Graphics.GraphicsDeviceException>().FirstOrDefault() is { } deviceError)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(deviceError).Throw();
            Execute(() => throw error);
        }
    }
}
