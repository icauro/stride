using Stride.Input;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static readonly Keys[] CameraBindingChoices = Enum.GetValues<Keys>().Distinct().ToArray();
    private void DrawCameraBindingSettings()
    {
        settingsCameraBindings.MoveCamForward = DrawCameraKeySetting("Move camera forward", settingsCameraBindings.MoveCamForward);
        settingsCameraBindings.MoveCamBackward = DrawCameraKeySetting("Move camera backward", settingsCameraBindings.MoveCamBackward);
        settingsCameraBindings.MoveCamLeft = DrawCameraKeySetting("Move camera left", settingsCameraBindings.MoveCamLeft);
        settingsCameraBindings.MoveCamRight = DrawCameraKeySetting("Move camera right", settingsCameraBindings.MoveCamRight);
        settingsCameraBindings.MoveCamUpward = DrawCameraKeySetting("Move camera up", settingsCameraBindings.MoveCamUpward);
        settingsCameraBindings.MoveCamDownward = DrawCameraKeySetting("Move camera down", settingsCameraBindings.MoveCamDownward);
        settingsCameraBindings.IncreaseCamSpeed = DrawCameraKeySetting("Increase camera speed", settingsCameraBindings.IncreaseCamSpeed);
        settingsCameraBindings.DecreaseCamSpeed = DrawCameraKeySetting("Decrease camera speed", settingsCameraBindings.DecreaseCamSpeed);
        settingsCameraBindings.CenterViewOnSelection = DrawCameraKeySetting("Center view on selection", settingsCameraBindings.CenterViewOnSelection);
        settingsCameraBindings.SnapSelectionToGrid = DrawCameraKeySetting("Snap selection to the grid", settingsCameraBindings.SnapSelectionToGrid);
        settingsCameraBindings.ControlDynamicSnapSelectionToGrid = DrawCameraKeySetting("Use snapping while selection is beeing manipulated", settingsCameraBindings.ControlDynamicSnapSelectionToGrid);
        settingsCameraBindings.TranslationGizmo = DrawCameraKeySetting("Switch to translation mode", settingsCameraBindings.TranslationGizmo);
        settingsCameraBindings.RotationGizmo = DrawCameraKeySetting("Switch to rotation mode", settingsCameraBindings.RotationGizmo);
        settingsCameraBindings.ScaleGizmo = DrawCameraKeySetting("Switch to scale mode", settingsCameraBindings.ScaleGizmo);
        settingsCameraBindings.SwitchGizmo = DrawCameraKeySetting("Switch to next gizmo mode", settingsCameraBindings.SwitchGizmo);
        DrawSettingsBoolean("Invert mouse panning axis", ref settingsInvertPanningAxis);
    }
    private Keys DrawCameraKeySetting(string label, Keys value)
    {
        BeginSettingsProperty(label); Gui.SetNextItemWidth(-1);
        if (Gui.BeginCombo("##Value", value.ToString()))
        {
            foreach (var key in CameraBindingChoices)
                if (Gui.Selectable(key.ToString(), key == value)) value = key;
            Gui.EndCombo();
        }
        Gui.PopID(); return value;
    }
    private bool IsDynamicSceneSnapDown() => CameraBindingDown((preferences?.Current ?? FallbackSceneEditorPreferences).CameraBindings.ControlDynamicSnapSelectionToGrid);
    private bool CameraBindingDown(Keys key) => ImGuiInput.TryMapKey(key, out var mapped) ? Gui.IsKeyDown(mapped) : Input.IsKeyDown(key);
    private bool CameraBindingPressed(Keys key) => ImGuiInput.TryMapKey(key, out var mapped) ? Gui.IsKeyPressed(mapped, false) : Input.IsKeyPressed(key);
}
