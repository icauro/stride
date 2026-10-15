using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    internal bool WorkflowLayoutResetPending => resetLayout;

    internal unsafe WorkflowToolWindow[] InspectWorkflowToolWindows()
    {
        var windows = new List<WorkflowToolWindow>();
        foreach (var panel in EditorPanels.All)
        {
            var window = ImGuiP.FindWindowByName(panel.DisplayWindowName);
            if (window.IsNull || Gui.GetFrameCount() - window.LastFrameActive is not (0 or 1)) continue;
            windows.Add(new(panel.CaptureId, panel.Title, window.ID, window.DockId,
                window.Size.X, window.Size.Y, window.DockIsActive));
        }
        return windows.ToArray();
    }
}

internal sealed record WorkflowToolWindow(string Id, string Title, uint WindowId, uint DockId, float Width, float Height, bool Docked);

internal sealed partial class UiWorkflowTest
{
    private SceneDocument? layoutScene;
    private Stride.Core.Mathematics.Matrix layoutCamera;

    private void UpdateLayoutScenario()
    {
        switch (stage)
        {
            case 10800:
                if (game.WorkflowLayoutResetPending) break;
                AssertToolWindowSeparation();
                layoutScene = game.Document; layoutCamera = game.SceneCameraView;
                Assert(layoutScene is { IsDirty: false }, "Layout fixture must have a real clean document");
                if (Click("menu/view")) Next(); break;
            case 10801: if (Click("menu/view/property-grid")) Next(); break;
            case 10802:
                if (game.CaptureInspectionSnapshot().Panels.Single(panel => panel.Id == "editor.properties").RequestedVisible) break;
                checks.Add("View menu hides the actual Property grid tool window");
                if (Click("menu/view")) Next(); break;
            case 10803: if (Click("menu/view/reset-layout")) Next(); break;
            case 10804:
                if (game.WorkflowLayoutResetPending
                    || !game.CaptureInspectionSnapshot().Panels.Single(panel => panel.Id == "editor.properties").RequestedVisible) break;
                AssertToolWindowSeparation();
                Assert(ReferenceEquals(game.Document, layoutScene) && !layoutScene!.IsDirty
                    && game.SceneCameraView == layoutCamera,
                    "Reset layout must preserve the canonical document, clean data and actual scene camera");
                checks.Add("Reset layout restores distinct tool windows and retains authored owner/camera");
                CompleteIndependentSubset(); break;
        }
    }

    private void AssertToolWindowSeparation()
    {
        string[] required = ["assets.browser", "editor.output", "assets.references", "assets.errors", "editor.properties"];
        var actual = game.InspectWorkflowToolWindows();
        var windows = required.Select(id => actual.SingleOrDefault(window => window.Id == id)
            ?? throw new InvalidOperationException("Missing actual tool window: " + id)).ToArray();
        Assert(windows.Select(window => window.WindowId).Distinct().Count() == required.Length,
            "Asset view/Output/References/Asset errors/Property grid must be separate native ImGui windows");
        Assert(windows.All(window => window.Docked && window.DockId != 0 && float.IsFinite(window.Width)
            && float.IsFinite(window.Height) && window.Width > 0 && window.Height > 0),
            "Default/reset tool windows must have valid native dock ownership and nonempty bounds");
    }
}
