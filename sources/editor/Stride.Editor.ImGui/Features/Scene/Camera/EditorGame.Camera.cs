using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool cameraGesture;
    private readonly object sceneCameraCaptureOwner = new();
    private bool cameraPointerMoved;
    private System.Numerics.Vector2? pickPress;
    private System.Numerics.Vector2 pickMotion;
    private bool pickToggle;
    private EntityHierarchyDocument? pickDocument;
    internal System.Numerics.Vector2 SceneImageMinimum, SceneImageSize;
    internal Vector2 EntityScreenPosition(string name) => engineViewport!.EntityScreenPosition(name);
    internal int PickingReadbacks => engineViewport?.Picker?.ReadbackCount ?? 0;
    internal double PickingReadbackMilliseconds => engineViewport?.Picker?.LastReadbackMilliseconds ?? 0;
    internal int SelectionDrawCalls => engineViewport?.SelectionRenderer?.LastDrawCalls ?? 0;
    internal int SelectedRuntimeModels => engineViewport?.SelectedModelCount ?? 0;
    internal int PreparedSelectionNodes => engineViewport?.PreparedSelectionNodes ?? 0;
    internal int CancelledPickRequests => engineViewport?.Picker?.CancelledRequests ?? 0;
    private bool showSceneSelection = true;
    internal bool SceneShowSelection => showSceneSelection;
    private void PumpScenePicking()
    {
        UpdatePickingOptions();
        var picker = engineViewport?.Picker;
        if (picker?.HasResult != true || document == null) return;
        if (CompleteSceneMaterialPick(picker)) return;
        picker.Consume();
        var requestedDocument = pickDocument; pickDocument = null;
        if (requestedDocument != null && requestedDocument != document) return;
        if (picker.Error != null) { statusMessage = picker.Error; return; }
        var picked = engineViewport?.ResolvePickedSelection(picker);
        // Original Ctrl+empty-space click leaves entity and folder selection entirely untouched.
        if (picked == null && pickToggle) return;
        if (picked is { } hit && engineViewport?.CanPickSceneEntity(hit) == false) return;
        if (picked is { } childEntity && engineViewport?.RuntimeOwner(childEntity) is SceneDocument childScene
            && !ReferenceEquals(childScene, document))
        {
            var selection = childEntity.Id;
            Execute(() =>
            {
                ActivateSceneRoot(childScene);
                SelectCompositeHierarchyEntity(childScene, childScene.Asset.Hierarchy.Parts.TryGetValue(selection, out var selected) ? selected.Entity : null, pickToggle);
            });
            return;
        }
        FinishEntityRename(true); document.EndEdits(); assetDocument?.EndEdit(); assetCommandContext = false;
        SelectCompositeHierarchyEntity(document, picked != null && document.Asset.Hierarchy.Parts.TryGetValue(picked.Id, out var part) ? part.Entity : null, pickToggle);
        statusMessage = document.Selected == null ? "Selection cleared" : "Selected " + document.Selected.Name;
    }
    internal Matrix SceneCameraView => engineViewport!.Camera.Component.ViewMatrix;
    internal Matrix SceneCameraProjection => engineViewport!.Camera.Component.ProjectionMatrix;
    internal EditorCameraOrientation? SceneOrientation => engineViewport!.Camera.Orientation;
    internal (float Fov, float Near, float Far) SceneLens => (engineViewport!.Camera.Component.VerticalFieldOfView,
        engineViewport.Camera.Component.NearClipPlane, engineViewport.Camera.Component.FarClipPlane);
    private string? cameraLensError;
    internal bool HasCameraLensError => cameraLensError != null;
    internal bool SceneOrthographic => engineViewport!.Camera.Orthographic;
    internal float SceneOrthographicSize => engineViewport!.Camera.OrthographicSize;
    internal bool SelectedFitsCamera => engineViewport!.SelectedFitsCamera(document!.Selected!);
    private void DrawSceneCameraToolbar(SceneToolbarContext? context = null)
    {
        if (context != null && !context.Current()) return;
        var engineViewport = context?.Viewport ?? this.engineViewport;
        engineViewport!.CameraPreviewFailure ??= error => { statusMessage = error.Message; RecordAssetPreviewRenderFailure("camera-preview", error); };
        if (SceneToolbarIcon("scene-toolbar-camera", "Editor camera options...")) { if (context == null) cameraLensError = null; else context.LensError = null; Gui.OpenPopup("Camera lens"); }
        if (Controls.Enabled) Controls.Record("camera/lens");
        if (!Gui.BeginPopup("Camera lens")) return;
        Gui.BeginDisabled(context == null ? CompositeHierarchyPrimary == null : context.Document.Selected == null);
        if (icons!.Button("focus", "Focus selected entity (F)"))
        {
            if (context == null) FocusScenePrimarySelection();
            else if (context.Current() && context.Document.Selected is { } selected) engineViewport!.Focus(context.Document, selected);
        }
        if (Controls.Enabled) Controls.Record("camera/focus"); Gui.EndDisabled(); Gui.SameLine();
        if (icons.Button("camera-reset", "Reset editor camera")) engineViewport!.Camera.Reset();
        if (Controls.Enabled) Controls.Record("camera/reset");
        var preview = engineViewport!.ShowCameraPreview;
        if (Gui.Checkbox(EditorLocalization.Widget("Camera preview", "Button"), ref preview)) engineViewport.ShowCameraPreview = preview;
        if (Controls.Enabled) Controls.Record("camera/preview");
        Gui.SeparatorText(EditorLocalization.Text("Projection")); Gui.SetNextItemWidth(UiScale(160));
        if (Gui.BeginCombo("##Projection", engineViewport.Camera.Orthographic ? "Orthographic" : "Perspective"))
        {
            if (Gui.Selectable(EditorLocalization.Widget("Perspective", "Button"), !engineViewport.Camera.Orthographic)) engineViewport.Camera.Orthographic = false;
            if (Controls.Enabled) Controls.Record("camera/projection/Perspective");
            if (Gui.Selectable(EditorLocalization.Widget("Orthographic", "Button"), engineViewport.Camera.Orthographic)) engineViewport.Camera.Orthographic = true;
            if (Controls.Enabled) Controls.Record("camera/projection/Orthographic"); Gui.EndCombo();
        }
        if (Controls.Enabled) Controls.Record("camera/projection");
        var lens = (Fov: engineViewport!.Camera.Component.VerticalFieldOfView, Near: engineViewport.Camera.Component.NearClipPlane, Far: engineViewport.Camera.Component.FarClipPlane);
        Gui.SetNextItemWidth(UiScale(160));
        if (Gui.InputFloat("Near plane", ref lens.Near, 0, 0, "%.4g")) UpdateCameraLens(lens, context);
        if (Controls.Enabled) Controls.Record("camera/lens/near");
        Gui.SetNextItemWidth(UiScale(160));
        if (Gui.InputFloat("Far plane", ref lens.Far, 0, 0, "%.6g")) UpdateCameraLens(lens, context);
        if (Controls.Enabled) Controls.Record("camera/lens/far");
        if (!engineViewport.Camera.Orthographic)
        {
            Gui.SetNextItemWidth(UiScale(160));
            if (Gui.InputFloat("Field of view", ref lens.Fov, 0, 0, "%.1f")) UpdateCameraLens(lens, context);
            if (Controls.Enabled) Controls.Record("camera/lens/fov");
        }
        if ((context == null ? cameraLensError : context.LensError) is { } lensError) DrawSelectableText("##CameraLensError", lensError);
        if (engineViewport.Camera.Orthographic)
        {
            var size = engineViewport.Camera.OrthographicSize; Gui.SetNextItemWidth(UiScale(110));
            if (Gui.InputFloat("Orthographic size", ref size, 0, 0, "%.4f")) Execute(() => engineViewport.Camera.OrthographicSize = size);
            if (Controls.Enabled) Controls.Record("camera/orthographic-size");
        }
        Gui.SeparatorText(EditorLocalization.Text("Movement"));
        Gui.TextUnformatted($"Speed: {engineViewport!.Camera.MoveSpeed:G} x");
        var speedIndex = engineViewport.Camera.MoveSpeedIndex; Gui.SetNextItemWidth(UiScale(160));
        if (Gui.SliderInt("##MovementSpeed", ref speedIndex, 0, Stride.Engine.Design.EditorCameraMovement.AvailableSpeeds.Length - 1, ""))
            engineViewport.Camera.MoveSpeedIndex = speedIndex;
        Gui.SeparatorText(EditorLocalization.Text("Orientation")); Gui.SetNextItemWidth(UiScale(160));
        if (Gui.BeginCombo("##Orientation", engineViewport.Camera.Orientation?.ToString() ?? "Free view"))
        {
            foreach (var orientation in EditorViewportCamera.Orientations)
            {
                if (Gui.Selectable(orientation.ToString(), engineViewport.Camera.Orientation == orientation)) engineViewport.Camera.Orient(orientation);
                if (Controls.Enabled) Controls.Record("camera/orientation/" + orientation);
            }
            Gui.EndCombo();
        }
        if (Controls.Enabled) Controls.Record("camera/orientation");
        Gui.EndPopup();
    }
    private void DrawSceneSelectionToolbar(SceneToolbarContext? context = null)
    {
        if (context != null && !context.Current()) return;
        var engineViewport = context?.Viewport ?? this.engineViewport;
        var showSceneSelection = context == null ? this.showSceneSelection : engineViewport!.ShowSelection;
        if (SceneToolbarIcon("scene-toolbar-selection", "Show or hide selection mask", showSceneSelection)) showSceneSelection = !showSceneSelection;
        engineViewport!.ShowSelection = showSceneSelection;
        if (context == null) this.showSceneSelection = showSceneSelection;
        if (Controls.Enabled) Controls.Record("scene/selection-wireframe");
    }
    private void HandleSceneCameraInput(System.Numerics.Vector2 size)
    {
        if (Controls.Enabled) Controls.Record("scene/image");
        SceneImageMinimum = Gui.GetItemRectMin(); SceneImageSize = size;
        var wasCaptured = platformViewports?.IsCameraCaptureOwned(sceneCameraCaptureOwner) == true;
        var nativeMotion = platformViewports?.ConsumeCameraMotion(sceneCameraCaptureOwner) ?? System.Numerics.Vector2.Zero;
        if (wasCaptured && platformViewports!.CameraCaptureMotion.LengthSquared() > Gui.GetIO().MouseDragThreshold * Gui.GetIO().MouseDragThreshold) cameraPointerMoved = true;
        if (WindowsSourceTextDragDrop.IsDragging || IsScriptOleFeedbackTick || Gui.GetIO().WantTextInput
            || !Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
        { CancelSceneCameraNavigation(); return; }
        if (engineViewport?.IsGamePreview == true)
        { CancelSceneCameraNavigation(); return; }
        if (projectLoading != null || closeRequested || closePromptOpen)
        { CancelSceneCameraNavigation(); engineViewport?.CameraOrientation?.CancelInteraction(); return; }
        var navigationSceneRadius = engineViewport?.OrthographicNavigationSceneRadius();
        if (navigationSceneRadius is { } sceneRadius) engineViewport!.Camera.UpdateOrthographicSceneDistance(sceneRadius);
        var bindings = (preferences?.Current ?? FallbackSceneEditorPreferences).CameraBindings;
        var io = Gui.GetIO(); var hovered = Gui.IsItemHovered();
        if (Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && !Gui.IsAnyItemActive() && !io.WantTextInput)
        {
            if (CameraBindingPressed(bindings.IncreaseCamSpeed) || Gui.IsKeyPressed(ImGuiKey.KeypadAdd)) engineViewport?.Camera.StepMovementSpeed(1);
            else if (CameraBindingPressed(bindings.DecreaseCamSpeed) || Gui.IsKeyPressed(ImGuiKey.KeypadSubtract)) engineViewport?.Camera.StepMovementSpeed(-1);
        }
        if (engineViewport?.CameraOrientation?.Handle(SceneImageMinimum, size, hovered,
                !cameraGesture && engineViewport.Gizmos?.Captured != true) == true)
        { CancelSceneCameraNavigation(); engineViewport.ClearMaterialHighlight(); engineViewport.Picker?.Cancel("Camera orientation interaction"); return; }
        if (document != null && engineViewport?.RuntimeSourceAvailable == true
            && engineViewport.Gizmos?.Handle(document, SceneImageMinimum, size, hovered && !cameraGesture && platformViewports?.HasCameraCapture != true) == true)
        { CancelSceneCameraNavigation(); engineViewport.ClearMaterialHighlight(); engineViewport.Picker?.Cancel("Gizmo interaction"); return; }
        if (hovered && Gui.IsMouseClicked(ImGuiMouseButton.Left) && !io.KeyAlt)
        { pickPress = io.MousePos; pickMotion = System.Numerics.Vector2.Zero; }
        if (pickPress != null)
        {
            // Native selection measures the complete path, including motion back toward the press.
            var motion = wasCaptured ? nativeMotion : io.MouseDelta;
            pickMotion += new System.Numerics.Vector2(Math.Abs(motion.X), Math.Abs(motion.Y));
        }
        if (Gui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            var clickThreshold = Stride.Assets.Presentation.AssetEditors.Gizmos.TransformationGizmo.TransformationStartPixelThreshold;
            if (hovered && pickPress != null && pickMotion.LengthSquared() < clickThreshold * clickThreshold
                && !Gui.IsMouseDown(ImGuiMouseButton.Right) && !io.KeyAlt && !cameraGesture)
            {
                var minimum = Gui.GetItemRectMin();
                var normalized = (io.MousePos - minimum) / size;
                if (engineViewport!.Picker is { DisplayedSize.X: > 0, DisplayedSize.Y: > 0 } picker)
                {
                    pickToggle = io.KeyCtrl; pickDocument = document;
                    UpdatePickingOptions();
                    var materialClick = sceneMaterialSelection && !pickToggle;
                    picker.Request(new Vector2(normalized.X, normalized.Y), picker.DisplayedViewProjection, picker.DisplayedSize, cycle: !pickToggle && !materialClick,
                        purpose: materialClick ? Stride.Engine.Design.EditorPickingPurpose.MaterialClick : Stride.Engine.Design.EditorPickingPurpose.Selection);
                }
            }
            pickPress = null;
        }
        var left = Gui.IsMouseDown(ImGuiMouseButton.Left);
        var middle = Gui.IsMouseDown(ImGuiMouseButton.Middle);
        var right = Gui.IsMouseDown(ImGuiMouseButton.Right);
        if (!left && !middle && !right) { cameraGesture = false; platformViewports?.EndCameraCapture(sceneCameraCaptureOwner); }
        if (hovered && ((Gui.IsMouseClicked(ImGuiMouseButton.Left) && io.KeyAlt) || Gui.IsMouseClicked(ImGuiMouseButton.Middle) || Gui.IsMouseClicked(ImGuiMouseButton.Right))) cameraGesture = true;
        if (hovered && !Gui.IsAnyItemActive() && !io.WantTextInput && CameraBindingPressed(bindings.CenterViewOnSelection)) FocusScenePrimarySelection();
        if (hovered && !Gui.IsAnyItemActive() && !io.WantTextInput && !io.KeyCtrl && !cameraGesture)
            HandleSceneTransformBindings(bindings);
        RequestSceneMaterialHover(size, hovered);
        if ((!hovered && !cameraGesture) || io.KeyCtrl || io.WantTextInput || Gui.IsKeyPressed(ImGuiKey.Escape, false))
        { CancelSceneCameraNavigation(); return; }
        var controlsPointer = cameraGesture && (middle && !right || !io.KeyAlt && right
            || io.KeyAlt && left && !middle && !right || io.KeyAlt && right && !left && !middle);
        if (controlsPointer && platformViewports is { HasCameraCapture: false } platform)
        {
            cameraPointerMoved = false;
            platform.BeginCameraCapture(Gui.GetWindowViewport().ID, sceneCameraCaptureOwner);
        }
        else if (!controlsPointer) platformViewports?.EndCameraCapture(sceneCameraCaptureOwner);
        var pointerDelta = wasCaptured ? nativeMotion : io.MouseDelta;
        var movement = new Vector3(
            (CameraBindingDown(bindings.MoveCamRight) || Gui.IsKeyDown(ImGuiKey.RightArrow) ? 1 : 0) - (CameraBindingDown(bindings.MoveCamLeft) || Gui.IsKeyDown(ImGuiKey.LeftArrow) ? 1 : 0),
            (CameraBindingDown(bindings.MoveCamUpward) || Gui.IsKeyDown(ImGuiKey.PageUp) ? 1 : 0) - (CameraBindingDown(bindings.MoveCamDownward) || Gui.IsKeyDown(ImGuiKey.PageDown) ? 1 : 0),
            (CameraBindingDown(bindings.MoveCamBackward) || Gui.IsKeyDown(ImGuiKey.DownArrow) ? 1 : 0) - (CameraBindingDown(bindings.MoveCamForward) || Gui.IsKeyDown(ImGuiKey.UpArrow) ? 1 : 0));
        engineViewport!.Camera.Navigate(new Vector2(pointerDelta.X / size.X, pointerDelta.Y / size.Y), hovered || cameraGesture ? io.MouseWheel : 0,
            cameraGesture && left, cameraGesture && middle, cameraGesture && right, io.KeyAlt, io.KeyShift, movement, io.DeltaTime, navigationSceneRadius);
    }
    internal void CancelSceneCameraNavigation()
    {
        CancelSceneDocumentCameraNavigation();
        cameraGesture = false; pickPress = null; sceneContextPress = null;
        platformViewports?.EndCameraCapture(sceneCameraCaptureOwner);
    }
    private void FocusScenePrimarySelection()
    {
        if (CompositeHierarchyPrimary is { } primary)
            engineViewport?.Focus(primary.Document, primary.Entity);
    }
    private void UpdateCameraLens((float Fov, float Near, float Far) lens, SceneToolbarContext? context = null)
    {
        if (context != null && !context.Current()) return;
        var viewport = context?.Viewport ?? engineViewport;
        try
        {
            viewport!.Camera.SetLens(lens.Fov, lens.Near, lens.Far);
            if (context == null) cameraLensError = null; else context.LensError = null;
        }
        catch (ArgumentException error)
        { if (context == null) cameraLensError = error.Message; else context.LensError = error.Message; }
    }
}


