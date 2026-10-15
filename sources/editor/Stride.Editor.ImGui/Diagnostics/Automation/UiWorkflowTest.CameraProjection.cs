using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class UiWorkflowTest
{
    private object? cameraProjectionEvidence;
    private Matrix projectionView, priorProjection;
    private float priorOrthoSize;
    private int orientationIndex;
    private readonly List<string> verifiedOrientations = [];
    private Matrix persistedView;
    private float persistedSize;
    private byte[]? preferenceSceneBytes;
    private int lensFieldIndex;
    private bool restoringLens;
    private string LensField => lensFieldIndex == 0 ? "fov" : lensFieldIndex == 1 ? "near" : "far";
    private string LensInput => restoringLens ? new[] { "45", "0.1", "1000" }[lensFieldIndex] : new[] { "60", "0.25", "500", "0" }[lensFieldIndex];
    private void UpdateCameraProjectionWorkflow()
    {
        var io = Gui.GetIO();
        var center = game.SceneImageMinimum + game.SceneImageSize * 0.5f;
        switch (stage)
        {
            case 50: if (Click("entity/Imported cube")) Next(); break;
            case 51:
                projectionView = game.SceneCameraView; priorProjection = game.SceneCameraProjection;
                if (Click("camera/projection")) Next(); break;
            case 52: if (Click("camera/projection/Orthographic")) Next(); break;
            case 53:
                Assert(game.SceneOrthographic && game.SceneCameraView == projectionView && game.SceneCameraProjection != priorProjection,
                    "Projection selector must update the actual engine camera projection without moving it");
                if (Click("camera/orthographic-size")) Next(); break;
            case 54: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 55:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var c in "6.5") io.AddInputCharacter(c); Next(); break;
            case 56: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 57: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 58:
                Assert(game.SceneOrthographicSize == 6.5f, "Size text must change real orthographic camera size");
                priorProjection = game.SceneCameraProjection;
                io.AddMousePosEvent(center.X, center.Y); io.AddMouseWheelEvent(0, 1); Next(); break;
            case 59:
                Assert(game.SceneOrthographicSize < 6.5f && game.SceneCameraView == projectionView && game.SceneCameraProjection != priorProjection,
                    "Orthographic wheel must zoom projection without changing camera position/view");
                priorOrthoSize = game.SceneOrthographicSize;
                io.AddMouseButtonEvent(2, true); Next(); break;
            case 60: io.AddMousePosEvent(center.X + 30, center.Y + 20); Next(); break;
            case 61: io.AddMouseButtonEvent(2, false); Next(); break;
            case 62:
                Assert(game.SceneCameraView != projectionView && game.SceneOrthographicSize == priorOrthoSize,
                    "Orthographic middle pan must move the real camera without changing zoom size");
                if (Click("camera/focus")) Next(); break;
            case 63:
                Assert(game.SceneOrthographic && game.SelectedFitsCamera && !game.Document!.IsDirty && !game.Document.CanUndo,
                    "Orthographic framing must fit actual compiled model bounds and preserve clean authored history");
                game.CaptureOrthographicCheckpoint(); ClickScenePoint(game.EntityScreenPosition("Imported cube")); Next(); break;
            case 64:
                if (game.PickingReadbacks == 4)
                {
                    Assert(game.Document!.Selected!.Name == "Imported cube" && !game.Document.IsDirty, "Orthographic GPU picking must resolve actual authored model identity");
                    cameraProjectionEvidence = new { orthographicSize = game.SceneOrthographicSize, fitsCamera = game.SelectedFitsCamera,
                        gpuPickReadbacks = game.PickingReadbacks, documentClean = !game.Document.IsDirty };
                    orientationIndex = 0; stage = 70; wait = 2;
                }
                break;
            case 70: if (Click("camera/orientation")) { stage = 71; wait = 2; } break;
            case 71:
                if (Click("camera/orientation/" + EditorViewportCamera.Orientations[orientationIndex])) { stage = 72; wait = 2; } break;
            case 72:
                var orientation = EditorViewportCamera.Orientations[orientationIndex];
                var direction = Vector3.Normalize(Vector3.TransformNormal(new Vector3(0, 0, -1), Matrix.Invert(game.SceneCameraView)));
                Assert(game.SceneOrientation == orientation && Vector3.Distance(direction, EditorViewportCamera.Direction(orientation)) < 0.00001f
                    && game.SceneOrthographic && game.SelectedFitsCamera && !game.Document!.IsDirty && !game.Document.CanUndo,
                    "Named view must use the correct finite engine camera direction, preserve framing/projection and leave authored data clean");
                verifiedOrientations.Add(orientation.ToString());
                if (++orientationIndex < EditorViewportCamera.Orientations.Length) stage = 70; else stage = 73; wait = 2; break;
            case 73: if (Click("camera/orientation")) { stage = 74; wait = 2; } break;
            case 74: if (Click("camera/orientation/Top")) { stage = 75; wait = 2; } break;
            case 75: game.CaptureTopViewCheckpoint(); ClickScenePoint(game.EntityScreenPosition("Imported cube")); stage = 76; wait = 2; break;
            case 76:
                if (game.PickingReadbacks == 5)
                {
                    Assert(game.Document!.Selected!.Name == "Imported cube", "Top orthographic view must pick the actual imported model");
                    cameraProjectionEvidence = new { orthographicSize = game.SceneOrthographicSize, fitsCamera = game.SelectedFitsCamera,
                        gpuPickReadbacks = game.PickingReadbacks, documentClean = !game.Document.IsDirty, orientations = verifiedOrientations };
                    persistedView = game.SceneCameraView; persistedSize = game.SceneOrthographicSize;
                    preferenceSceneBytes = File.ReadAllBytes(game.Document.Path);
                    if (Click("scene/selection-wireframe")) { stage = 90; wait = 2; }
                }
                break;
            case 90: if (Click("toolbar/reload")) { stage = 91; wait = 2; } break;
            case 91:
                Assert(game.SceneCameraView == persistedView && game.SceneOrientation == EditorCameraOrientation.Top && game.SceneOrthographic
                    && game.SceneOrthographicSize == persistedSize && !game.SceneShowSelection && !game.Document!.IsDirty && !game.Document.CanUndo
                    && File.ReadAllBytes(game.Document.Path).SequenceEqual(preferenceSceneBytes!),
                    "Scene reload must restore local camera preferences while preserving authored scene bytes and clean history");
                if (Click("scene/selection-wireframe")) { stage = 92; wait = 2; } break;
            case 92: if (Click("camera/projection")) { stage = 65; wait = 2; } break;
            case 65: if (Click("camera/projection/Perspective")) Next(); break;
            case 66: if (Click("camera/reset")) Next(); break;
            case 67:
                Assert(!game.SceneOrthographic && game.SceneCameraView == pickingCamera && !game.Document!.IsDirty && !game.Document.CanUndo,
                    "Perspective/reset must recover the original camera view without editing scene data/history");
                checks.Add("Real orthographic projection/size, position-preserving wheel zoom, middle pan, compiled-bounds framing, GPU picking and perspective restoration");
                checks.Add("All six named engine camera directions preserve framing/projection/history; exact vertical Top GPU picking and reset recovery");
                checks.Add("Scene reload restores exact Top orthographic camera and selection preference without changes to authored scene bytes/history");
                priorProjection = game.SceneCameraProjection;
                if (Click("camera/lens")) { lensFieldIndex = 0; restoringLens = false; stage = 100; wait = 2; } break;
            case 100: if (Click("camera/lens/" + LensField)) { stage = 101; wait = 2; } break;
            case 101: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); stage = 102; wait = 2; break;
            case 102:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var c in LensInput) io.AddInputCharacter(c); stage = 103; wait = 2; break;
            case 103: io.AddKeyEvent(ImGuiKey.Enter, true); stage = 104; wait = 2; break;
            case 104:
                io.AddKeyEvent(ImGuiKey.Enter, false);
                if (++lensFieldIndex < (restoringLens ? 3 : 4)) stage = 100; else stage = 105; wait = 2; break;
            case 105:
                if (!restoringLens)
                {
                    Assert(game.SceneLens == (60f, 0.25f, 500f) && game.HasCameraLensError && game.SceneCameraView == pickingCamera && game.SceneCameraProjection != priorProjection
                        && !game.Document!.IsDirty && !game.Document.CanUndo, "Lens edits must update actual values; invalid far input must retain valid camera/history and show feedback");
                    game.CaptureLensCheckpoint(); preferenceSceneBytes = File.ReadAllBytes(game.Document.Path);
                }
                stage = 109; wait = 2; break;
            case 109: io.AddKeyEvent(ImGuiKey.Escape, true); stage = 106; wait = 2; break;
            case 106: io.AddKeyEvent(ImGuiKey.Escape, false); stage = 107; wait = 2; break;
            case 107: if (Click("toolbar/reload")) { stage = 108; wait = 2; } break;
            case 108:
                Assert(game.SceneLens == (restoringLens ? (45f, 0.1f, 1000f) : (60f, 0.25f, 500f))
                    && !game.Document!.IsDirty && File.ReadAllBytes(game.Document.Path).SequenceEqual(preferenceSceneBytes!),
                    "Scene reload must restore lens preferences while leaving authored scene bytes unchanged");
                if (!restoringLens)
                {
                    if (Click("camera/lens")) { restoringLens = true; lensFieldIndex = 0; stage = 100; wait = 2; }
                }
                else
                {
                    checks.Add("Live lens FOV/near/far, invalid clip feedback without mutation, local preference reload and default restoration preserve scene bytes/history");
                    stage = 120; wait = 2;
                }
                break;
        }
    }
}
