using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawSceneRenderModeSelector(SceneToolbarContext? context = null)
    {
        if (context != null && !context.Current()) return;
        var engineViewport = context?.Viewport ?? this.engineViewport;
        if (engineViewport == null) return;
        NextSceneToolbarItem(UiScale(80)); Gui.SetNextItemWidth(UiScale(80));
        if (Gui.BeginCombo("##SceneRenderMode", engineViewport.SceneRenderMode.Name))
        {
            var index = 0;
            foreach (var mode in engineViewport.AvailableSceneRenderModes)
            {
                if (index++ == 2) Gui.Separator();
                if (Gui.Selectable(mode.Name, ReferenceEquals(mode, engineViewport.SceneRenderMode)))
                    engineViewport.SetSceneRenderMode(mode);
            }
            Gui.EndCombo();
        }
        RegisterVisualCaptureItem("scene.render-mode", "control", "Render mode");
    }
}
