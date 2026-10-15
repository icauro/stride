using System.Text.Json;

namespace Stride.Editor.ImGui;

internal sealed partial class UiWorkflowTest
{
    private readonly Queue<WorkflowAction> recentActions = new();
    private int observedStage = int.MinValue;
    private TimeSpan stageStarted;
    private string? lastMissingControl;
    private string? pendingClickId;
    private System.Numerics.Vector2 pendingClickPoint;
    private int pendingClickButton;
    private string? lastSubmittedControl;
    private readonly Queue<object> actualUiActions = new();
    private readonly Queue<object> actualOutputInput = new();
    internal void ObserveOutputInteraction(object value)
    {
        if (lastSubmittedControl != "output/actual-diagnostic" || Hexa.NET.ImGui.ImGui.GetFrameCount() > mousePressFrame + 8) return;
        if (actualOutputInput.Count == 32) actualOutputInput.Dequeue();
        actualOutputInput.Enqueue(value);
    }
    private readonly Dictionary<string, bool> actualModalStates = new();

    internal void ObserveUiAction(string id, bool activated)
    {
        var frame = Hexa.NET.ImGui.ImGui.GetFrameCount();
        if (id.StartsWith("modal/", StringComparison.Ordinal))
        {
            if (actualModalStates.TryGetValue(id, out var previous) && previous == activated) return;
            actualModalStates[id] = activated;
        }
        else if (!activated && pendingClickId != id && (lastSubmittedControl != id || frame > mousePressFrame + 8)) return;
        var io = Hexa.NET.ImGui.ImGui.GetIO();
        var context = Hexa.NET.ImGui.ImGui.GetCurrentContext();
        if (actualUiActions.Count == 32) actualUiActions.Dequeue();
        actualUiActions.Enqueue(new
        {
            id, activated, frame, stage,
            context.ActiveId, context.HoveredId, context.NavId,
            navWindow = context.NavWindow.IsNull ? 0 : context.NavWindow.ID,
            activeWindow = context.ActiveIdWindow.IsNull ? 0 : context.ActiveIdWindow.ID,
            hoveredWindow = context.HoveredWindow.IsNull ? 0 : context.HoveredWindow.ID,
            popupCount = context.OpenPopupStack.Size,
            mouseDown = Hexa.NET.ImGui.ImGui.IsMouseDown(Hexa.NET.ImGui.ImGuiMouseButton.Left),
            mouseClicked = Hexa.NET.ImGui.ImGui.IsMouseClicked(Hexa.NET.ImGui.ImGuiMouseButton.Left),
            mouseReleased = Hexa.NET.ImGui.ImGui.IsMouseReleased(Hexa.NET.ImGui.ImGuiMouseButton.Left),
            mouseX = io.MousePos.X, mouseY = io.MousePos.Y,
        });
    }
    private CancellationToken cancellationToken;

    // Called by the runner before Update; cancellation is observed on the editor actor.
    internal void SetCancellationToken(CancellationToken token) => cancellationToken = token;

    private void CheckProgressDeadline()
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (observedStage != stage)
        {
            observedStage = stage;
            stageStarted = timer.Elapsed;
            lastMissingControl = null;
            RecordAction("stage", stage.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        // Cold restore/compile is a real asynchronous prerequisite. Cancellation
        // and owner retirement have separate bounded deadlines, even after the
        // loading UI disappears. None of these clocks reset on progress messages.
        var limit = TimeSpan.FromSeconds(stage switch
        {
            10002 or 10404 or 10408 => 150, // actual cold load/failure/retry publication
            10003 or 10102 or 10103 => 60, // compiler cancellation/exit and canonical adoption
            10106 => 60, // protected project close and native ownership retirement
            _ => game.IsProjectLoading || stage is 2 or -2 or 37 or 38 ? 60 : 30,
        });
        // Leave the independent runner's 240-second external watchdog time to
        // collect failure diagnostics and execute teardown; retain legacy budget.
        var totalLimit = TimeSpan.FromSeconds(scenario == UiWorkflowScenario.Legacy ? 90 : 220);
        if (timer.Elapsed - stageStarted > limit || timer.Elapsed > totalLimit)
            throw new TimeoutException($"UI stage {stage} did not complete within {limit.TotalSeconds}s (run limit {totalLimit.TotalSeconds}s). "
                + $"Missing control: {lastMissingControl ?? "none"}. {game.StatusMessage}");
    }

    private void RecordAction(string kind, string target, float? targetX = null, float? targetY = null, float? mouseX = null, float? mouseY = null)
    {
        if (recentActions.Count == 64) recentActions.Dequeue();
        recentActions.Enqueue(new WorkflowAction(timer.Elapsed.TotalSeconds, stage, kind, target, targetX, targetY, mouseX, mouseY));
    }

    private void Fail(Exception error)
    {
        Finished = true;
        Passed = false;
        Result = $"UI E2E FAIL (stage {stage}): {error.Message}. {game.StatusMessage}";
        try { ReportInspectorGroupFailure(); }
        catch (Exception groupError) { Result += " Group report failed: " + groupError.Message; }
        try { RestoreReferenceClipboard(); }
        catch (Exception clipboardError) { Result += " Clipboard restore failed: " + clipboardError.Message; }
        // Diagnostics must never replace the primary test failure.
        try
        {
            var root = Environment.GetEnvironmentVariable("STRIDE_EDITOR_TEST_ARTIFACTS");
            if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(AppContext.BaseDirectory, "ui-failures");
            var directory = Path.Combine(Path.GetFullPath(root), "failure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "failure.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                error = error.ToString(),
                stage,
                elapsedSeconds = timer.Elapsed.TotalSeconds,
                lastMissingControl,
                fixture = game.Document?.Path,
                actions = recentActions.ToArray(),
                inputPhase = new { releaseMouse, releaseMouseButton, mousePressFrame,
                    guiFrame = Hexa.NET.ImGui.ImGui.GetFrameCount(), wait,
                    mouseDown = Hexa.NET.ImGui.ImGui.IsMouseDown(Hexa.NET.ImGui.ImGuiMouseButton.Left) },
                actualUiActions = actualUiActions.ToArray(),
                actualOutputInput = actualOutputInput.ToArray(),
                toolWindows = game.InspectWorkflowToolWindows(),
                checks = checks.ToArray(),
                state = game.CaptureInspectionSnapshot(true),
                recentOutput = game.CaptureWorkflowRecentOutput(),
                browser = game.InspectAssetBrowserWorkflow(),
                material = game.InspectMaterialWorkflow(),
                output = game.InspectOutputWorkflow(),
                outputGeometry = game.InspectWorkflowOutputGeometry(CompileFailureMarker),
                interactionGates = game.InspectWorkflowInteractionGates(),
                propertyScroll = game.InspectWorkflowPropertyScroll(),
                runtimeReferenceMismatches = game.CaptureRuntimeReferenceMismatchesVerification(),
                hierarchyCreationEligibility = game.WorkflowHierarchyCreationEligibility,
                runtime = Environment.Version.ToString(),
                operatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                adapter = game.GraphicsDevice.Adapter.DriverInfo,
                spriteAuthored = game.InspectSpriteAuthoredName(),
                uiAuthored = game.InspectUiAuthoredName(),
                capture = "The runner capture sidecar identifies the retained rendered editor frame; an empty teardown frame is not UI evidence.",
            }, new JsonSerializerOptions { WriteIndented = true }));
            var trace = WorkflowTracePath;
            if (File.Exists(trace)) File.Copy(trace, Path.Combine(directory, "ui-trace.log"));
            Result += " Diagnostics: " + directory;
        }
        catch (Exception diagnosticError) { Result += " Diagnostics failed: " + diagnosticError.Message; }
    }

    private sealed record WorkflowAction(double Seconds, int Stage, string Kind, string Target,
        float? TargetX, float? TargetY, float? MouseX, float? MouseY);
}
