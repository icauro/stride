using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void HandleSceneDocumentCameraInput(SceneToolbarContext context, System.Numerics.Vector2 size)
    {
        var viewport = context.Viewport;
        var platform = platformViewports;
        var captured = platform?.IsCameraCaptureOwned(context.CameraCaptureOwner) == true;
        var motion = platform?.ConsumeCameraMotion(context.CameraCaptureOwner) ?? System.Numerics.Vector2.Zero;
        if (!context.Current() || !viewport.Visible || viewport.IsGamePreview || size.X <= 0 || size.Y <= 0
            || WindowsSourceTextDragDrop.IsDragging || IsScriptOleFeedbackTick || Gui.GetIO().WantTextInput
            || !Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) || projectLoading != null
            || closeRequested || closePromptOpen || closingDocument != null)
        { CancelSceneDocumentCameraNavigation(context, cancelOrientation: true); return; }
        var radius = viewport.OrthographicNavigationSceneRadius();
        if (radius is { } sceneRadius) viewport.Camera.UpdateOrthographicSceneDistance(sceneRadius);
        var bindings = (preferences?.Current ?? FallbackSceneEditorPreferences).CameraBindings;
        var io = Gui.GetIO(); var hovered = Gui.IsItemHovered(); var origin = Gui.GetItemRectMin();
        if (!Gui.IsAnyItemActive() && !io.WantTextInput)
        {
            if (CameraBindingPressed(bindings.IncreaseCamSpeed) || Gui.IsKeyPressed(ImGuiKey.KeypadAdd)) viewport.Camera.StepMovementSpeed(1);
            else if (CameraBindingPressed(bindings.DecreaseCamSpeed) || Gui.IsKeyPressed(ImGuiKey.KeypadSubtract)) viewport.Camera.StepMovementSpeed(-1);
        }
        if (viewport.CameraOrientation?.Handle(origin, size, hovered, !context.CameraGesture) == true)
        {
            CancelSceneDocumentCameraNavigation(context); viewport.ClearMaterialHighlight();
            viewport.Picker?.Cancel("Camera orientation interaction"); return;
        }
        var left = Gui.IsMouseDown(ImGuiMouseButton.Left);
        var middle = Gui.IsMouseDown(ImGuiMouseButton.Middle);
        var right = Gui.IsMouseDown(ImGuiMouseButton.Right);
        if (!left && !middle && !right) { context.CameraGesture = false; platform?.EndCameraCapture(context.CameraCaptureOwner); }
        if (hovered && ((Gui.IsMouseClicked(ImGuiMouseButton.Left) && io.KeyAlt)
            || Gui.IsMouseClicked(ImGuiMouseButton.Middle) || Gui.IsMouseClicked(ImGuiMouseButton.Right))) context.CameraGesture = true;
        if (hovered && !Gui.IsAnyItemActive() && !io.WantTextInput && CameraBindingPressed(bindings.CenterViewOnSelection)
            && context.Document.Selected is { } selected) viewport.Focus(context.Document, selected);
        if ((!hovered && !context.CameraGesture) || io.KeyCtrl || io.WantTextInput || Gui.IsKeyPressed(ImGuiKey.Escape, false))
        { CancelSceneDocumentCameraNavigation(context); return; }
        var controlsPointer = context.CameraGesture && (middle && !right || !io.KeyAlt && right
            || io.KeyAlt && left && !middle && !right || io.KeyAlt && right && !left && !middle);
        if (controlsPointer && platform is { HasCameraCapture: false })
            platform.BeginCameraCapture(Gui.GetWindowViewport().ID, context.CameraCaptureOwner);
        else if (!controlsPointer) platform?.EndCameraCapture(context.CameraCaptureOwner);
        if (controlsPointer && platform?.HasCameraCapture == true && !platform.IsCameraCaptureOwned(context.CameraCaptureOwner))
        { CancelSceneDocumentCameraNavigation(context); return; }
        var delta = captured ? motion : io.MouseDelta;
        var movement = new Vector3(
            (CameraBindingDown(bindings.MoveCamRight) || Gui.IsKeyDown(ImGuiKey.RightArrow) ? 1 : 0) - (CameraBindingDown(bindings.MoveCamLeft) || Gui.IsKeyDown(ImGuiKey.LeftArrow) ? 1 : 0),
            (CameraBindingDown(bindings.MoveCamUpward) || Gui.IsKeyDown(ImGuiKey.PageUp) ? 1 : 0) - (CameraBindingDown(bindings.MoveCamDownward) || Gui.IsKeyDown(ImGuiKey.PageDown) ? 1 : 0),
            (CameraBindingDown(bindings.MoveCamBackward) || Gui.IsKeyDown(ImGuiKey.DownArrow) ? 1 : 0) - (CameraBindingDown(bindings.MoveCamForward) || Gui.IsKeyDown(ImGuiKey.UpArrow) ? 1 : 0));
        viewport.Camera.Navigate(new Vector2(delta.X / size.X, delta.Y / size.Y), hovered || context.CameraGesture ? io.MouseWheel : 0,
            context.CameraGesture && left, context.CameraGesture && middle, context.CameraGesture && right,
            io.KeyAlt, io.KeyShift, movement, io.DeltaTime, radius);
    }
    private void CancelSceneDocumentCameraNavigation(SceneToolbarContext context, bool cancelOrientation = false)
    {
        context.CameraGesture = false;
        platformViewports?.EndCameraCapture(context.CameraCaptureOwner);
        if (cancelOrientation) context.Viewport.CameraOrientation?.CancelInteraction();
    }
    private void CancelSceneDocumentCameraNavigation(SceneDocumentView view)
    {
        if (sceneToolbarContexts.TryGetValue(view.Viewport, out var context)) CancelSceneDocumentCameraNavigation(context, cancelOrientation: true);
    }
    private void CancelSceneDocumentCameraNavigation()
    {
        foreach (var view in sceneDocumentViews.Values) CancelSceneDocumentCameraNavigation(view);
    }
}
