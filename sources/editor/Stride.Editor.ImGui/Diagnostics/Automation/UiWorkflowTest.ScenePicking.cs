using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class UiWorkflowTest
{
    private object? pickingEvidence;
    private readonly List<double> pickingTimings = [];
    private Matrix pickingCamera;
    private int sphereSelectionCalls, cubeSelectionCalls;
    private bool pickingTrickleEvents;
    private void ClickScenePoint(Vector2 normalized)
    {
        Assert(normalized.X >= 0 && normalized.X < 1 && normalized.Y >= 0 && normalized.Y < 1, "Projected fixture entity must be visible inside scene image");
        var point = game.SceneImageMinimum + new System.Numerics.Vector2(normalized.X, normalized.Y) * game.SceneImageSize;
        var io = Gui.GetIO(); io.AddMousePosEvent(point.X, point.Y); io.AddMouseButtonEvent(0, true); releaseMouse = true;
    }
    private void AssertSelection(string? name, int reads)
    {
        Assert(game.Document!.Selected?.Name == name && !game.Document.IsDirty && !game.Document.CanUndo
            && game.SceneCameraView == pickingCamera && game.PickingReadbacks == reads, "GPU picking must update authored selection without editing history or camera");
        pickingTimings.Add(game.PickingReadbackMilliseconds);
    }
    private void UpdateScenePickingWorkflow()
    {
        if (stage >= 170) { UpdateHierarchyMoveWorkflow(); return; }
        if (stage >= 140) { UpdateHierarchyDeletionWorkflow(); return; }
        if (stage >= 120) { UpdateHierarchyCreationWorkflow(); return; }
        if (stage >= 50) { UpdateCameraProjectionWorkflow(); return; }
        switch (stage)
        {
            case 0: if (Click("toolbar/open")) Next(); break;
            case 1: if (Click("project/open")) Next(); break;
            case 2:
                if (game.Document != null && game.EngineDrawCalls > 0 && Click("entity/Sphere")) { stage = 20; wait = 2; } break;
            case 20:
                if (game.SelectionDrawCalls > 0 && game.PreparedSelectionNodes > 0)
                {
                    Assert(game.Document!.Selected!.Name == "Sphere" && game.PickingReadbacks == 0 && game.SelectedRuntimeModels == 1,
                        "Hierarchy selection must drive real wireframe feedback without a GPU pick request");
                    if (Click("entity/Camera")) { stage = 21; wait = 2; }
                }
                break;
            case 21:
                Assert(game.SelectedRuntimeModels == 0 && game.SelectionDrawCalls == 0 && game.PreparedSelectionNodes == 0,
                    "Selecting a non-model entity must remove mesh feedback and its stage work");
                pickingTrickleEvents = Gui.GetIO().ConfigInputTrickleEventQueue;
                Gui.GetIO().ConfigInputTrickleEventQueue = false;
                ClickScenePoint(game.EntityScreenPosition("Sphere")); releaseMouse = false; stage = 30; wait = 2; break;
            case 30:
                // Same input frame releases the click and changes the camera before the GPU query.
                Gui.GetIO().AddMouseButtonEvent(0, false); Gui.GetIO().AddMouseWheelEvent(0, 1); stage = 31; wait = 2; break;
            case 31:
                Gui.GetIO().ConfigInputTrickleEventQueue = pickingTrickleEvents;
                Assert(game.CancelledPickRequests == 1 && game.PickingReadbacks == 0 && game.Document!.Selected!.Name == "Camera",
                    $"Camera movement after click release must cancel the queued actual picking request and retain selection (cancelled={game.CancelledPickRequests}, readbacks={game.PickingReadbacks}, selected={game.Document.Selected?.Name})");
                if (Click("camera/reset")) { stage = 32; wait = 2; } break;
            case 32: stage = 3; wait = 2; break;
            case 3:
                pickingCamera = game.SceneCameraView; ClickScenePoint(game.EntityScreenPosition("Sphere")); Next(); break;
            case 4:
                if (game.PickingReadbacks == 1 && game.SelectionDrawCalls > 0 && game.PreparedSelectionNodes > 0)
                {
                    AssertSelection("Sphere", 1); Assert(game.SelectedRuntimeModels == 1, "Wireframe filtering must include only the selected runtime model");
                    sphereSelectionCalls = game.SelectionDrawCalls; game.CapturePickingCheckpoint();
                    if (Click("scene/selection-wireframe")) { stage = 40; wait = 2; }
                }
                break;
            case 40:
                Assert(game.SelectionDrawCalls == 0 && game.PreparedSelectionNodes == 0 && game.Document!.Selected!.Name == "Sphere",
                    "Hiding selection feedback must stop its stage/preparation without clearing authored selection");
                if (Click("scene/selection-wireframe")) { stage = 41; wait = 2; } break;
            case 41:
                if (game.SelectionDrawCalls > 0 && game.PreparedSelectionNodes > 0)
                { Assert(game.PickingReadbacks == 1 && !game.Document!.IsDirty, "Showing wireframe must retain selection/history without another pick"); stage = 5; wait = 2; }
                break;
            case 5: ClickScenePoint(game.EntityScreenPosition("Imported cube")); Next(); break;
            case 6:
                if (game.PickingReadbacks == 2 && game.SelectionDrawCalls > 0 && game.PreparedSelectionNodes > 0)
                { AssertSelection("Imported cube", 2); cubeSelectionCalls = game.SelectionDrawCalls; game.CaptureSelectedCubeCheckpoint(); Next(); }
                break;
            case 7: ClickScenePoint(new Vector2(0.01f, 0.01f)); Next(); break;
            case 8:
                if (game.PickingReadbacks == 3)
                { AssertSelection(null, 3); Assert(game.SelectedRuntimeModels == 0 && game.SelectionDrawCalls == 0 && game.PreparedSelectionNodes == 0,
                    "Empty-space clear must remove all wireframe models and idle work"); stage = 9; wait = 30; }
                break;
            case 9:
                Assert(game.PickingReadbacks == 3, "Idle scene frames must not issue GPU picking readbacks");
                Gui.GetIO().AddKeyEvent(ImGuiKey.ModAlt, true); ClickScenePoint(game.EntityScreenPosition("Sphere")); Next(); break;
            case 10: Gui.GetIO().AddKeyEvent(ImGuiKey.ModAlt, false); Next(); break;
            case 11:
                Assert(game.PickingReadbacks == 3 && game.Document!.Selected == null, "Alt orbit input must not select or issue picking readbacks");
                if (Click("camera/reset")) Next(); break;
            case 12:
                var press = game.SceneImageMinimum + game.SceneImageSize * 0.5f;
                Gui.GetIO().AddMousePosEvent(press.X, press.Y); Gui.GetIO().AddMouseButtonEvent(0, true); Next(); break;
            case 13:
                var drag = game.SceneImageMinimum + game.SceneImageSize * 0.7f;
                Gui.GetIO().AddMousePosEvent(drag.X, drag.Y); Next(); break;
            case 14:
                var returned = game.SceneImageMinimum + game.SceneImageSize * 0.5f;
                Gui.GetIO().AddMousePosEvent(returned.X, returned.Y); Gui.GetIO().AddMouseButtonEvent(0, false); Next(); break;
            case 15:
                Assert(game.PickingReadbacks == 3 && game.Document!.Selected == null && !game.Document.IsDirty,
                    "A left drag beyond the click threshold must not pick or edit");
                pickingEvidence = new { requests = 3, readbacks = game.PickingReadbacks, readbackMilliseconds = pickingTimings,
                    cameraPreserved = game.SceneCameraView == pickingCamera, documentClean = !game.Document!.IsDirty,
                    selection = new { sphereDrawCalls = sphereSelectionCalls, cubeDrawCalls = cubeSelectionCalls, emptyDrawCalls = game.SelectionDrawCalls },
                    cancelledRequests = game.CancelledPickRequests,
                    boundary = "CPU wall time around synchronous 1x1 staging GetData only; not a WPF/total click latency comparison." };
                checks.Add("GPU mesh/depth picking of actual procedural and imported models; authored GUID selection, empty-space clear, clean history/camera, zero idle readbacks and no picking from Alt orbit/left drag");
                checks.Add("Real selected-mesh wireframe stage, GPU parameter preparation, hide/show retaining selection and zero preparation/draws on empty selection");
                checks.Add("Actual viewport click cancelled by same-frame camera movement without GPU readback or selection loss; reset and subsequent picks recover");
                stage = 50; wait = 2; break;
        }
    }
}
