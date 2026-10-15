using System.Numerics;
using System.Text.Json;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    internal bool IsWorkflowLayoutRestartRead
        => Array.IndexOf(args, "U07.layout-read") >= 0;

    internal string WorkflowLayoutPath => settingsPath;

    internal unsafe WorkflowRestartWindow? InspectRestartWindow(string identity)
    {
        var panel = EditorPanels.All.SingleOrDefault(item => item.CaptureId == identity);
        if (string.IsNullOrEmpty(panel.WindowName)) return null;
        var window = ImGuiP.FindWindowByName(panel.DisplayWindowName);
        if (window.IsNull || Gui.GetFrameCount() - window.LastFrameActive is not (0 or 1)) return null;
        return new(identity, window.ID, window.DockId, window.Pos.X, window.Pos.Y,
            window.Size.X, window.Size.Y, window.DockIsActive);
    }
}

internal sealed record WorkflowRestartWindow(string Identity, uint WindowId, uint DockId,
    float X, float Y, float Width, float Height, bool Docked);

internal sealed partial class UiWorkflowTest
{
    internal bool PersistLayoutForRestart
        => scenario is UiWorkflowScenario.U07LayoutWrite or UiWorkflowScenario.U07LayoutRead;

    private WorkflowRestartWindow? restartInitialWindow;
    private Vector2 restartTabPoint, restartDragPoint;
    private string RestartExpectationPath => Path.Combine(Path.GetDirectoryName(game.WorkflowLayoutPath)!, "layout-expectation.json");

    private void UpdateLayoutRestartScenario()
    {
        var io = Gui.GetIO();
        switch (stage)
        {
            case 11200:
                if (game.WorkflowLayoutResetPending) break;
                if (scenario == UiWorkflowScenario.U07LayoutRead)
                {
                    Assert(File.Exists(game.WorkflowLayoutPath) && File.Exists(RestartExpectationPath),
                        "Restart read requires the completed prior process's native layout and expectation");
                    var expected = JsonSerializer.Deserialize<WorkflowRestartWindow>(File.ReadAllText(RestartExpectationPath))
                        ?? throw new InvalidDataException("Missing layout expectation");
                    var actual = game.InspectRestartWindow(expected.Identity);
                    if (actual == null) break;
                    Assert(actual.WindowId == expected.WindowId && !actual.Docked && actual.DockId == 0
                        && Math.Abs(actual.X - expected.X) <= 2 && Math.Abs(actual.Y - expected.Y) <= 2
                        && Math.Abs(actual.Width - expected.Width) <= 2 && Math.Abs(actual.Height - expected.Height) <= 2,
                        $"Normal startup must restore the dragged native window identity/bounds: expected={expected}, actual={actual}");
                    Assert(game.Document is { IsDirty: false } && game.UsesCompiledProjectContent,
                        "Restored tool layout must coexist with a newly loaded real clean workspace");
                    checks.Add("Independent process restores actual dragged Property grid identity, floating dock state and bounds from normal terminal save");
                    CompleteIndependentSubset();
                    break;
                }
                restartInitialWindow = game.InspectRestartWindow("editor.properties");
                if (restartInitialWindow == null || game.InspectDockTabCenter("editor.properties") is not { } tab) break;
                Assert(restartInitialWindow.Docked && restartInitialWindow.DockId != 0,
                    "Private fresh layout must begin with a genuinely docked Property grid");
                restartTabPoint = tab;
                var viewport = Gui.GetMainViewport();
                restartDragPoint = viewport.WorkPos + new Vector2(viewport.WorkSize.X * .4f, viewport.WorkSize.Y * .35f);
                io.AddMousePosEvent(tab.X, tab.Y); Next(); break;
            case 11201:
                // Shift reverses the native docking activation policy. Disable docking
                // while dragging so release creates a movable floating tool window.
                io.AddKeyEvent(ImGuiKey.ModShift, !io.ConfigDockingWithShift);
                io.AddMouseButtonEvent(0, true); Next(); break;
            case 11202:
                io.AddMousePosEvent(restartTabPoint.X - 45, restartTabPoint.Y + 45); Next(); break;
            case 11203:
                io.AddMousePosEvent(restartDragPoint.X, restartDragPoint.Y); Next(); break;
            case 11204:
                io.AddMouseButtonEvent(0, false); Next(); break;
            case 11205:
                io.AddKeyEvent(ImGuiKey.ModShift, false); Next(); break;
            case 11206:
                var floated = game.InspectRestartWindow("editor.properties");
                if (floated == null) break;
                Assert(!floated.Docked && floated.DockId == 0 && floated.WindowId == restartInitialWindow!.WindowId
                    && float.IsFinite(floated.X) && float.IsFinite(floated.Y)
                    && floated.Width > 0 && floated.Height > 0,
                    "Actual title-tab mouse drag must undock the same Property grid before persistence");
                Assert(Math.Abs(floated.X - restartInitialWindow.X) > 10 || Math.Abs(floated.Y - restartInitialWindow.Y) > 10,
                    "Dragged window must differ from the default placement");
                File.WriteAllText(RestartExpectationPath, JsonSerializer.Serialize(floated));
                checks.Add("Actual title-tab drag changes native dock ownership and bounds; normal terminal save is enabled for this private restart fixture");
                CompleteIndependentSubset(); break;
        }
    }
}
