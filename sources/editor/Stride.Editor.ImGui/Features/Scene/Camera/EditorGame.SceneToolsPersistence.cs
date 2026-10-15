using Stride.Assets.Presentation.SceneEditor;
using Stride.Core.Mathematics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void RestoreNativeSceneToolsSettings(SceneSettingsData native, SceneToolbarContext? context = null)
    {
        var engineViewport = context?.Viewport ?? this.engineViewport;
        if (engineViewport?.Grid is { } grid)
        {
            var settings = grid.Settings with
            {
                Visible = native.GridVisible, Color = new(native.GridColor.R, native.GridColor.G, native.GridColor.B),
                Opacity = native.GridOpacity, Axes = native.GridAxisIndex, Spacing = engineViewport.Camera.SceneUnit,
            };
            if (settings.Valid) grid.ApplySettings(settings);
        }
        var transform = new SceneTransformSettings(native.TranslationSnapActive, native.RotationSnapActive, native.ScaleSnapActive,
            native.TranslationSnapValue, native.RotationSnapValue, native.ScaleSnapValue, (float)native.TransformationGizmoSize);
        if (transform.Valid && engineViewport?.Gizmos is { } gizmos)
        { gizmos.RestoreSettings(transform); if (context == null) configuredGizmos = gizmos; }
        engineViewport?.ComponentGizmos?.RestoreNativeSettings(native.ComponentGizmoSize, native.FixedSizeGizmos, native.HiddenGizmos ?? []);
    }
    private bool NativeSceneToolsMatch(SceneSettingsData native, SceneToolbarContext? context = null)
    {
        var engineViewport = context?.Viewport ?? this.engineViewport;
        if (engineViewport?.Grid is { } grid && (native.GridVisible != grid.Settings.Visible
            || native.GridColor != new Color3(grid.Settings.Color.X, grid.Settings.Color.Y, grid.Settings.Color.Z)
            || native.GridOpacity != grid.Settings.Opacity || native.GridAxisIndex != grid.Settings.Axes)) return false;
        if (engineViewport?.Gizmos is { } gizmos)
        {
            var settings = gizmos.CaptureSettings();
            if (native.TranslationSnapActive != settings.TranslationActive || native.RotationSnapActive != settings.RotationActive
                || native.ScaleSnapActive != settings.ScaleActive || native.TranslationSnapValue != settings.TranslationValue
                || native.RotationSnapValue != settings.RotationValue || native.ScaleSnapValue != settings.ScaleValue
                || native.TransformationGizmoSize != settings.Size) return false;
        }
        if (engineViewport?.ComponentGizmos is { } components)
        {
            var settings = components.CaptureSettings();
            if (native.ComponentGizmoSize != settings.Size || native.FixedSizeGizmos != settings.FixedSize
                || !(native.HiddenGizmos ?? []).Order(StringComparer.Ordinal).SequenceEqual(components.CaptureNativeHiddenGizmos(native.HiddenGizmos ?? []).Order(StringComparer.Ordinal))) return false;
        }
        return true;
    }
    private void CaptureNativeSceneToolsSettings(SceneSettingsData native, SceneToolbarContext? context = null)
    {
        var engineViewport = context?.Viewport ?? this.engineViewport;
        if (engineViewport?.Grid is { } grid)
        {
            native.GridVisible = grid.Settings.Visible; native.GridColor = new Color3(grid.Settings.Color.X, grid.Settings.Color.Y, grid.Settings.Color.Z);
            native.GridOpacity = grid.Settings.Opacity; native.GridAxisIndex = grid.Settings.Axes;
        }
        if (engineViewport?.Gizmos is { } gizmos)
        {
            var settings = gizmos.CaptureSettings();
            native.TranslationSnapActive = settings.TranslationActive; native.TranslationSnapValue = settings.TranslationValue;
            native.RotationSnapActive = settings.RotationActive; native.RotationSnapValue = settings.RotationValue;
            native.ScaleSnapActive = settings.ScaleActive; native.ScaleSnapValue = settings.ScaleValue;
            native.TransformationGizmoSize = settings.Size;
        }
        if (engineViewport?.ComponentGizmos is { } components)
        {
            var settings = components.CaptureSettings(); native.ComponentGizmoSize = settings.Size; native.FixedSizeGizmos = settings.FixedSize;
            native.HiddenGizmos = components.CaptureNativeHiddenGizmos(native.HiddenGizmos ?? []).ToList();
        }
    }
}
