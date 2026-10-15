using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

/// <summary>Independent E02 picking subset, reusing native GPU-picking assertions from the existing workflow.</summary>
internal sealed class EnginePickingCapabilityProbe : EngineUiCapabilityProbe
{
    private Matrix camera;
    private Guid expectedEntity;
    private int reads, canceled;
    private long resetRenderRevision;
    private bool trickle, changedTrickle;
    protected override string Scenario => "E02";
    protected override object Evidence => new { expectedEntity, reads = Game.VerificationPickingReadbacks,
        canceled = Game.VerificationCancelledPickRequests, gesture = Game.CapturePickingGestureVerification(),
        gaps = new[] { "Gizmo gestures and document/view stale-request invalidation are separate integration cases." } };
    internal EnginePickingCapabilityProbe(EditorGame game, string artifacts) : base(game, artifacts) { }
    protected override void CleanInput()
    { if (changedTrickle) Gui.GetIO().ConfigInputTrickleEventQueue = trickle; }
    protected override void Step()
    {
        var io = Gui.GetIO();
        switch (Phase)
        {
            case 0: if (Click("toolbar/open")) Next(); break;
            case 1: if (Click("project/open")) Next(); break;
            case 2:
                if (Game.Document != null && EnsureScenePresented() && Click("entity/Camera")) Next();
                break;
            case 3:
                if (Game.Document?.Selected?.Name != "Camera") break;
                reads = Game.VerificationPickingReadbacks; canceled = Game.VerificationCancelledPickRequests;
                expectedEntity = Game.Document.Asset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Sphere").Entity.Id;
                if (!changedTrickle) { trickle = io.ConfigInputTrickleEventQueue; changedTrickle = true; io.ConfigInputTrickleEventQueue = false; }
                // Cancellation does not require a model hit: use a viewport
                // corner away from the selected Camera's native transform handles.
                if (ScenePress(new Vector2(.03f, .85f), false)) Next(); break;
            case 4:
                io.AddMouseButtonEvent(0, false); io.AddMouseWheelEvent(0, 1); Next(); break;
            case 5:
                if (Game.VerificationCancelledPickRequests <= canceled) break;
                Require(Game.VerificationPickingReadbacks == reads && Game.Document!.Selected?.Name == "Camera",
                    "Camera movement must cancel the queued real pick without GPU readback or selection mutation.");
                io.ConfigInputTrickleEventQueue = trickle; changedTrickle = false;
                if (Click("camera/reset")) { resetRenderRevision = Game.VerificationSceneRenderRevision; Next(); } break;
            case 6:
                if (!EnsureScenePresented() || !Game.VerificationPickingCameraCurrent
                    || Game.VerificationSceneRenderRevision <= resetRenderRevision) break;
                camera = Game.VerificationSceneCamera; if (ScenePress(Game.VerificationEntityScreenPosition("Sphere"), true)) Next(); break;
            case 7:
                if (Game.VerificationPickingReadbacks <= reads || Game.VerificationPickingResultPending) break;
                Require(Game.VerificationPickingReadbacks == reads + 1 && Game.Document!.Selected?.Id == expectedEntity,
                    "Actual GPU picking must select the fixture's authored GUID exactly once.");
                Require(Game.VerificationSelectedRuntimeModels == 1 && Game.VerificationPreparedSelectionNodes > 0 && Game.VerificationSelectionDrawCalls > 0,
                    "Picked runtime model must enter the actual selection-render stage.");
                Require(!Game.Document.IsDirty && !Game.Document.CanUndo && Game.VerificationSceneCamera == camera,
                    "Picking must preserve authored history and camera.");
                Game.SaveSceneVerificationOutput(Path.Combine(Artifacts, "E02-selected.png"));
                if (ScenePress(new Vector2(.01f, .01f), true)) Next(); break;
            case 8:
                if (Game.VerificationPickingReadbacks <= reads + 1 || Game.VerificationPickingResultPending) break;
                Require(Game.VerificationPickingReadbacks == reads + 2 && Game.Document!.Selected == null,
                    "Empty-space actual GPU pick must clear selection exactly once.");
                Require(Game.VerificationSelectedRuntimeModels == 0 && Game.VerificationSelectionDrawCalls == 0 && Game.VerificationPreparedSelectionNodes == 0
                    && !Game.Document.IsDirty && Game.VerificationSceneCamera == camera,
                    "Empty selection must remove selection-stage work without authored changes.");
                Finish(true, "E02 picking subset PASS: real target GUID, camera-stale cancellation, empty-space clear and unchanged history/camera. Gizmo coverage remains a gap.");
                break;
        }
    }
}

internal sealed partial class EditorGame
{
    internal object CapturePickingGestureVerification() => new
    {
        commandOwnerMatchesView = ReferenceEquals(engineViewport, InspectionViewport),
        press = pickPress is { } point ? new { x = point.X, y = point.Y } : null,
        motion = new { x = pickMotion.X, y = pickMotion.Y },
        cameraGesture,
        gizmoCaptured = InspectionViewport?.Gizmos?.Captured,
        pending = InspectionViewport?.Picker?.IsPending,
        unconsumedResult = InspectionViewport?.Picker?.HasResult,
        pickerError = InspectionViewport?.Picker?.Error,
        selectedName = document?.Selected?.Name,
        selectedId = document?.Selected?.Id,
        displayedSize = InspectionViewport?.Picker is { } picker ? new { x = picker.DisplayedSize.X, y = picker.DisplayedSize.Y } : null,
        sceneMinimum = new { x = SceneImageMinimum.X, y = SceneImageMinimum.Y },
        sceneSize = new { x = SceneImageSize.X, y = SceneImageSize.Y },
        wantTextInput = Gui.GetIO().WantTextInput,
        mouse = new { x = Gui.GetIO().MousePos.X, y = Gui.GetIO().MousePos.Y },
        leftDown = Gui.IsMouseDown(ImGuiMouseButton.Left),
    };
}
