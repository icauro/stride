using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class UIEditorPanel
{
    private void DrawCanvasViewCommands(UIDocument source)
    {
        if (!host.BeginMenu("View", viewport != null)) return;
        try
        {
            if (host.DrawMenuItem("Zoom in")) QueueCameraCommand(source, target => target.StepZoom(-1));
            if (host.DrawMenuItem("Zoom out")) QueueCameraCommand(source, target => target.StepZoom(1));
            if (host.DrawMenuItem("Reset camera")) QueueCameraCommand(source, target => target.ResetCamera());
        }
        finally { Gui.EndMenu(); }
    }
    private void QueueCameraCommand(UIDocument source, Action<UIEditorViewport> apply)
    {
        var target = viewport;
        if (target == null) return;
        QueueUICommand(source, [], false, (_, _) =>
        {
            if (!ReferenceEquals(viewport, target) || target.RenderError != null) return;
            StopCameraInput(); CancelCanvasPointer(); StopLayoutDrag();
            apply(target);
        });
    }
}
