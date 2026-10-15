using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class SceneToolbarContext
    {
        internal required SceneDocumentView View;
        internal EngineViewport Viewport => View.Viewport;
        internal EntityHierarchyDocument Document => View.Document;
        internal required Func<bool> OwnerCurrent;
        internal required Func<bool> Current;
        internal required Func<bool> CanAuthor;
        internal Action? SaveViewSettings;
        internal string? LensError;
        internal bool PendingGridSave;
        internal int LightProbeBounces = 1;
        internal ScenePickingOptions Picking = new();
        internal readonly object CameraCaptureOwner = new();
        internal bool CameraGesture;
    }
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<EngineViewport, SceneToolbarContext> sceneToolbarContexts = new();
    private SceneToolbarContext GetSceneToolbarContext(SceneDocumentView view, bool restoreViewSettings = true)
        => sceneToolbarContexts.GetValue(view.Viewport, _ =>
        {
            var history = sessionHistory;
            bool OwnerCurrent() => sceneDocumentViews.TryGetValue(view.Entry.Id, out var current) && ReferenceEquals(current, view)
                && IsCurrentSceneDocumentView(view) && history != null && ReferenceEquals(history, sessionHistory);
            var context = new SceneToolbarContext
            {
                View = view,
                OwnerCurrent = OwnerCurrent,
                Current = () => OwnerCurrent() && !IsEditorGpuRetirementPending && !IsEditorGpuRetirementFinalizing && !IsEditorGraphicsRecovering,
                CanAuthor = () => CanStartAssetEdit && ReferenceEquals(documents.Active, view.Entry)
                    && ReferenceEquals(document, view.Document) && ReferenceEquals(engineViewport, view.Viewport)
                    && IsCurrentSceneDocumentView(view) && view.Viewport.RuntimeSourceAvailable,
            };
            context.SaveViewSettings = () => SaveNativeSceneDisplaySettings(context);
            if (restoreViewSettings && !ReferenceEquals(engineViewport, view.Viewport)) RestoreNativeSceneDisplaySettings(context);
            return context;
        });
    private void SaveSceneDocumentViewSettingsBeforeRetirement()
    {
        foreach (var view in sceneDocumentViews.Values.ToArray())
        {
            if (!IsCurrentSceneDocumentView(view)) continue;
            SaveSceneDocumentViewControlSettings(view);
        }
    }
    private void SaveSceneDocumentViewControlSettings(SceneDocumentView view)
    {
        if (IsRecoveryWorkspacePresentationRestoring) return;
        if (!IsCurrentSceneDocumentView(view)) return;
        var context = GetSceneToolbarContext(view);
        if (ReferenceEquals(engineViewport, view.Viewport)) context.LightProbeBounces = lightProbeBounces;
        SaveNativeSceneDisplaySettings(context, retiring: true);
        context.PendingGridSave = false;
    }
    private void CaptureSceneDocumentControlState()
    {
        var view = sceneDocumentViews.Values.FirstOrDefault(value => ReferenceEquals(value.Viewport, engineViewport));
        if (view == null || !IsCurrentSceneDocumentView(view)) return;
        var context = GetSceneToolbarContext(view);
        context.LightProbeBounces = lightProbeBounces; context.LensError = cameraLensError;
        context.Picking = scenePickingOptions;
        SaveSceneMaterialSelectionState(); SaveSceneTransformationViewState();
    }
    private void RestoreSceneDocumentControlState(EditorDocumentEntry entry)
    {
        if (!sceneDocumentViews.TryGetValue(entry.Id, out var view) || !IsCurrentSceneDocumentView(view)
            || !ReferenceEquals(view.Viewport, engineViewport) || !ReferenceEquals(document, view.Document)) return;
        if (!sceneToolbarContexts.TryGetValue(view.Viewport, out var context))
        {
            RestoreSceneCameraSettings();
            context = GetSceneToolbarContext(view);
            context.LightProbeBounces = lightProbeBounces; context.LensError = cameraLensError;
        }
        else
        {
            lightProbeBounces = context.LightProbeBounces; cameraLensError = context.LensError;
            showSceneSelection = view.Viewport.ShowSelection;
            sceneMaterialSelection = view.Viewport.MaterialSelectionEnabled;
            configuredGizmos = view.Viewport.Gizmos;
            SaveSceneMaterialSelectionState(); SaveSceneTransformationViewState();
        }
        scenePickingOptions = context.Picking;
        view.Viewport.SetPickingTypeFilter(context.Picking.Predicate, context.Picking.Version);
    }
    private void TransferSceneDocumentControlContext(SceneDocumentView previous, SceneDocumentView next)
    {
        CancelSceneDocumentCameraNavigation(previous);
        if (!ReferenceEquals(previous.Entry, next.Entry) || !ReferenceEquals(previous.Document, next.Document)
            || !ReferenceEquals(previous.State, next.State) || !ReferenceEquals(previous.Workspace, next.Workspace)
            || !sceneToolbarContexts.TryGetValue(previous.Viewport, out var before)) return;
        if (ReferenceEquals(engineViewport, next.Viewport) || ReferenceEquals(engineViewport, previous.Viewport))
        { before.LightProbeBounces = lightProbeBounces; before.LensError = cameraLensError; before.Picking = scenePickingOptions; }
        var after = GetSceneToolbarContext(next, restoreViewSettings: false);
        after.LightProbeBounces = before.LightProbeBounces; after.LensError = before.LensError;
        after.Picking = before.Picking; after.PendingGridSave = before.PendingGridSave;
        next.Viewport.MaterialSelectionEnabled = previous.Viewport.MaterialSelectionEnabled;
        next.Viewport.SetPickingTypeFilter(after.Picking.Predicate, after.Picking.Version);
        if (ReferenceEquals(engineViewport, next.Viewport))
        {
            scenePickingOptions = after.Picking; sceneMaterialSelection = next.Viewport.MaterialSelectionEnabled;
            lightProbeBounces = after.LightProbeBounces; cameraLensError = after.LensError;
            SaveSceneMaterialSelectionState(); SaveSceneTransformationViewState();
        }
    }
    private void RunSceneToolbarAuthoring(SceneToolbarContext? context, Action action)
    {
        if (context == null) { action(); return; }
        if (!context.Current() || !context.CanAuthor()) return;
        ValidatePendingPropertyStrings(); documents.CommitAll();
        if (!context.Current() || !context.CanAuthor())
            throw new InvalidOperationException("The scene command owner changed.");
        action();
    }
    private void DrawSceneToolbar(SceneToolbarContext? context = null)
    {
        if (context != null && !context.Current()) return;
        if (!HasSceneContentArea()) return;
        var toolbarMinimum = Gui.GetCursorScreenPos();
        var toolbarWidth = Gui.GetContentRegionAvail().X;
        Gui.PushFont(Gui.GetFont(), 12);
        Gui.PushStyleVar(ImGuiStyleVar.FramePadding, UiScale(3, 2));
        Gui.PushStyleVar(ImGuiStyleVar.ItemSpacing, UiScale(2, 2));
        // The outer group registers the full two-part toolbar bounds and
        // advances layout from its actual maximum height, even without an image.
        Gui.BeginGroup();
        Gui.SetCursorScreenPos(toolbarMinimum + new NVector2(0, UiScale(2)));
        BeginSceneToolbar(toolbarMinimum.X + toolbarWidth);
        Gui.BeginGroup();
        DrawSceneRenderModeSelector(context); SceneToolbarSeparator();
        DrawTransformGizmoToolbar(context);
        DrawSceneMaterialSelectionToolbar(context); DrawSceneSelectionToolbar(context);
        Gui.EndGroup();
        var leftMaximum = Gui.GetItemRectMax();
        var optionsWidth = (UiScale(16) + Gui.GetStyle().FramePadding.X * 2) * 5 + Gui.GetStyle().ItemSpacing.X * 4;
        var optionsStart = toolbarMinimum.X + toolbarWidth - optionsWidth;
        var optionsY = optionsStart >= leftMaximum.X + Gui.GetStyle().ItemSpacing.X ? toolbarMinimum.Y + UiScale(2) : Gui.GetCursorScreenPos().Y;
        Gui.SetCursorScreenPos(new NVector2(Math.Max(toolbarMinimum.X, optionsStart), optionsY));
        BeginSceneToolbar(toolbarMinimum.X + toolbarWidth);
        Gui.BeginGroup();
        DrawGridControls(context); DrawSceneLightingControls(context); DrawSceneNavigationControls(context); DrawComponentGizmoControls(context); DrawSceneCameraToolbar(context);
        Gui.EndGroup();
        Gui.EndGroup();
        RegisterVisualCaptureRegion("scene.toolbar", "toolbar", "Scene toolbar", toolbarMinimum,
            new NVector2(toolbarMinimum.X + toolbarWidth, Gui.GetCursorScreenPos().Y), "editor.document");
        Gui.PopStyleVar(2); Gui.PopFont();
        if (context != null && context.Current() && CanStartAssetEdit && !Gui.IsAnyItemActive()) context.SaveViewSettings?.Invoke();
    }

    private bool HasSceneContentArea()
    {
        var available = Gui.GetContentRegionAvail();
        var minimum = UiScale(28);
        return available.X >= minimum && available.Y >= minimum;
    }
}
