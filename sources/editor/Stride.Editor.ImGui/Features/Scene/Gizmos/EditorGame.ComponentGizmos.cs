using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawComponentGizmoControls(SceneToolbarContext? context = null)
    {
        if (context != null && !context.Current()) return;
        var engineViewport = context?.Viewport ?? this.engineViewport;
        if (engineViewport?.ComponentGizmos is not { } gizmos) return;
        if (SceneToolbarIcon("scene-toolbar-gizmos", "Grid and gizmo options...")) Gui.OpenPopup("Component gizmos");
        if (!Gui.BeginPopup("Component gizmos")) return;
        if (engineViewport.Gizmos is { } transform)
        {
            var settings = transform.CaptureSettings();
            Gui.TextUnformatted(EditorLocalization.Text("Transformation gizmo size:"));
            var size = settings.Size;
            if (DrawGizmoZoomSlider("##TransformGizmoSize", ref size)) transform.RestoreSettings(settings with { Size = size });
        }
        var componentSettings = gizmos.CaptureSettings();
        Gui.TextUnformatted(EditorLocalization.Text("Component gizmo size:"));
        var fixedSize = componentSettings.FixedSize;
        Gui.SameLine();
        if (Gui.Checkbox(EditorLocalization.Widget("Fixed", "Button"), ref fixedSize))
            gizmos.RestoreSettings(componentSettings = componentSettings with { FixedSize = fixedSize });
        var componentSize = componentSettings.Size;
        if (DrawGizmoZoomSlider("##ComponentGizmoSize", ref componentSize))
            gizmos.RestoreSettings(componentSettings with { Size = componentSize });
        var cameraPreview = engineViewport.ShowCameraPreview;
        if (Gui.Checkbox(EditorLocalization.Widget("Camera preview", "Button"), ref cameraPreview)) engineViewport.ShowCameraPreview = cameraPreview;
        if (engineViewport.LightProbeVolumes is { } volumes)
        {
            Gui.SameLine();
            var visible = volumes.Visible;
            if (Gui.Checkbox(EditorLocalization.Widget("Light probe volumes", "Button"), ref visible)) volumes.Visible = visible;
        }
        Gui.Separator();
        if (Gui.Button(EditorLocalization.Widget("All", "Button"))) foreach (var family in gizmos.Families) if (!family.IsFallback) gizmos.SetVisible(family, true);
        Gui.SameLine();
        if (Gui.Button(EditorLocalization.Widget("None", "Button"))) foreach (var family in gizmos.Families) if (!family.IsFallback) gizmos.SetVisible(family, false);
        foreach (var family in gizmos.Families)
        {
            if (family.IsFallback) continue;
            Gui.PushID(family.Id);
            var visible = gizmos.IsVisible(family);
            if (Gui.Checkbox(family.Name, ref visible)) gizmos.SetVisible(family, visible);
            Gui.PopID();
        }
        if (gizmos.Families.FirstOrDefault(family => family.IsFallback) is { } fallback)
        {
            Gui.PushID(fallback.Id);
            var visible = gizmos.IsVisible(fallback);
            if (Gui.Checkbox(EditorLocalization.Widget("Other entities", "Button"), ref visible)) gizmos.SetVisible(fallback, visible);
            Gui.PopID();
        }
        Gui.Separator(); DrawPickingTypeControls(context);
        Gui.EndPopup();
    }

    private bool DrawGizmoZoomSlider(string id, ref float value)
    {
        var factors = Stride.Core.Presentation.Core.Utils.ZoomFactors;
        var index = 0;
        for (var candidate = 1; candidate < factors.Length; candidate++)
            if (Math.Abs(factors[candidate] - value) < Math.Abs(factors[index] - value)) index = candidate;
        Gui.SetNextItemWidth(UiScale(250));
        Gui.PushStyleVar(Hexa.NET.ImGui.ImGuiStyleVar.FramePadding, UiScale(6, 3));
        var changed = Gui.SliderInt(id, ref index, 0, factors.Length - 1, "");
        Gui.PopStyleVar();
        if (changed) value = (float)factors[index];
        return changed;
    }
}
