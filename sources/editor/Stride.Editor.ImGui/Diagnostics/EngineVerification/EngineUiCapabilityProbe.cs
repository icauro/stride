using System.Diagnostics;
using System.Text.Json;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal abstract class EngineUiCapabilityProbe
{
    protected readonly EditorGame Game;
    protected readonly Stopwatch Clock = Stopwatch.StartNew();
    protected readonly string Artifacts;
    protected int Phase;
    private bool releaseMouse;
    private int mousePressFrame = -1, mouseReleaseFrame = -1;
    private int dismissCameraPopup;
    private string? pendingClickId;
    private System.Numerics.Vector2 pendingClickPoint;
    private string? lastMissingControl, progressWriteError;
    private double nextProgress;
    private int lastInputFrame = -1;
    private double phaseStarted;
    private readonly List<string> actions = [];
    private string? reportFixture;
    private object? reportEvidence;
    internal bool Finished { get; private set; }
    internal bool Passed { get; private set; }
    internal string Result { get; private set; } = "";
    internal string? SharedTerminalTeardownPath { get; set; }
    protected abstract string Scenario { get; }
    protected abstract object Evidence { get; }
    protected virtual double PhaseTimeoutSeconds => Phase <= 2 ? 180 : 45;
    protected EngineUiCapabilityProbe(EditorGame game, string artifacts)
    { Game = game; Artifacts = Path.GetFullPath(artifacts); Directory.CreateDirectory(Artifacts); }
    internal void Update()
    {
        if (Finished) return;
        WriteProgress();
        try
        {
            if (Clock.Elapsed.TotalSeconds - phaseStarted > PhaseTimeoutSeconds)
                throw new TimeoutException($"{Scenario} phase {Phase}: {Game.StatusMessage}; {Game.EngineDiagnostic}");
            var inputFrame = Gui.GetFrameCount();
            if (inputFrame == lastInputFrame) return;
            lastInputFrame = inputFrame;
            Gui.GetIO().AddFocusEvent(true);
            if (releaseMouse)
            {
                if (inputFrame <= mousePressFrame) return;
                // Reuse the bounded native event-trickling grace used by the
                // workflow harness: a press can follow pointer/focus events.
                if (!Gui.IsMouseDown(ImGuiMouseButton.Left) && inputFrame <= mousePressFrame + 1) return;
                actions.Add($"{Clock.Elapsed.TotalSeconds:F3}: release nativeDown={Gui.IsMouseDown(ImGuiMouseButton.Left)} pressFrame={mousePressFrame} releaseFrame={inputFrame}");
                Gui.GetIO().AddMouseButtonEvent(0, false); releaseMouse = false; mouseReleaseFrame = inputFrame;
                return;
            }
            if (mouseReleaseFrame >= 0)
            {
                if (inputFrame <= mouseReleaseFrame + 1) return;
                Require(!Gui.IsMouseDown(ImGuiMouseButton.Left) || inputFrame <= mouseReleaseFrame + 4,
                    "Synthetic mouse release was not processed within four actual native GUI frames");
                if (Gui.IsMouseDown(ImGuiMouseButton.Left)) return;
                mouseReleaseFrame = -1;
            }
            if (dismissCameraPopup != 0)
            {
                Gui.GetIO().AddKeyEvent(ImGuiKey.Escape, dismissCameraPopup == 2);
                dismissCameraPopup--; return;
            }
            var sourceNotice = Game.Controls.Centers.ContainsKey("external-change/keep") ? "external-change/keep"
                : Game.Controls.Centers.ContainsKey("script-reload/dont-reload") ? "script-reload/dont-reload" : null;
            if (sourceNotice != null)
            {
                // Controlled source failures also invoke the genuine source
                // watchers. Only the visible native notice can interrupt the test;
                // a deferred logical notice must not block an unrelated active modal.
                Click(sourceNotice);
                return;
            }
            Step();
        }
        catch (Exception error) { Finish(false, error.ToString()); }
    }
    protected abstract void Step();
    protected bool EnsureScenePresented()
    {
        if (!Game.VerificationSceneHasRuntime) return false;
        if (Game.VerificationScenePresented) return true;
        if (Game.RecordVerificationSceneDockTab()) Click("engine/scene-tab");
        return false;
    }
    protected bool Click(string id)
    {
        if (!Game.Controls.Centers.TryGetValue(id, out var point))
        {
            lastMissingControl = id;
            var tab = id.StartsWith("asset/", StringComparison.Ordinal) ? "tooltab/assets.browser"
                : id.StartsWith("sprite-preview/", StringComparison.Ordinal) || id.StartsWith("font-preview/", StringComparison.Ordinal)
                    || id.StartsWith("animation-preview/", StringComparison.Ordinal) || id.StartsWith("model-preview/", StringComparison.Ordinal)
                    ? "tooltab/assets.preview"
                : id.StartsWith("entity/", StringComparison.Ordinal) ? "tooltab/scene.hierarchy"
                : id is "camera/focus" or "camera/reset" ? "camera/lens" : null;
            if (tab != null) Click(tab); else pendingClickId = null;
            lastMissingControl = id;
            return false;
        }
        lastMissingControl = null;
        var io = Gui.GetIO();
        if (!Aim(id, point)) return false;
        io.AddMouseButtonEvent(0, true);
        mousePressFrame = Gui.GetFrameCount();
        if (id is "camera/focus" or "camera/reset") dismissCameraPopup = 2;
        releaseMouse = true; actions.Add($"{Clock.Elapsed.TotalSeconds:F3}: click {id}"); return true;
    }
    protected bool ScenePress(Stride.Core.Mathematics.Vector2 normalized, bool release)
    {
        Require(normalized.X >= 0 && normalized.X < 1 && normalized.Y >= 0 && normalized.Y < 1,
            "The fixture target must project inside the actual scene view.");
        var point = Game.SceneImageMinimum + new System.Numerics.Vector2(normalized.X, normalized.Y) * Game.SceneImageSize;
        var io = Gui.GetIO();
        if (!Aim("scene-press", point)) return false;
        io.AddMouseButtonEvent(0, true);
        mousePressFrame = Gui.GetFrameCount();
        releaseMouse = release; actions.Add($"{Clock.Elapsed.TotalSeconds:F3}: scene press {normalized}");
        return true;
    }
    protected bool Aim(string id, System.Numerics.Vector2 point)
    {
        var io = Gui.GetIO();
        if ((io.BackendFlags & ImGuiBackendFlags.HasMouseHoveredViewport) != 0)
            io.AddMouseViewportEvent(Game.Controls.Viewports.GetValueOrDefault(id));
        if (pendingClickId != id || pendingClickPoint != point || System.Numerics.Vector2.DistanceSquared(io.MousePos, point) > 2f)
        {
            pendingClickId = id; pendingClickPoint = point; io.AddMousePosEvent(point.X, point.Y);
            actions.Add($"{Clock.Elapsed.TotalSeconds:F3}: aim {id}"); return false;
        }
        pendingClickId = null; return true;
    }
    protected void Next() => SeekPhase(Phase + 1);
    protected void SeekPhase(int phase) { Phase = phase; phaseStarted = Clock.Elapsed.TotalSeconds; WriteProgress(true); }
    private void WriteProgress(bool force = false)
    {
        if (!force && Clock.Elapsed.TotalSeconds < nextProgress) return;
        nextProgress = Clock.Elapsed.TotalSeconds + 2;
        try
        {
            System.Numerics.Vector2? currentControlPoint = pendingClickId != null
                && Game.Controls.Centers.TryGetValue(pendingClickId, out var recordedPoint) ? recordedPoint : null;
            File.AppendAllText(Path.Combine(Artifacts, Scenario + "-progress.jsonl"), JsonSerializer.Serialize(new
            {
                schema = 1, utc = DateTimeOffset.UtcNow, process = Environment.ProcessId, phase = Phase,
                phaseSeconds = Clock.Elapsed.TotalSeconds - phaseStarted, elapsedSeconds = Clock.Elapsed.TotalSeconds,
                lastMissingControl, pendingClickId, releaseMouse, dismissCameraPopup, progressWriteError,
                pendingClickPoint = new { x = pendingClickPoint.X, y = pendingClickPoint.Y },
                mousePressFrame, mouseReleaseFrame, nativeMouseDown = Gui.IsMouseDown(ImGuiMouseButton.Left),
                actualCurrentControlPoint = currentControlPoint is { } actualPoint ? new { x = actualPoint.X, y = actualPoint.Y } : null,
                pendingPointDistanceSquared = currentControlPoint is { } pendingTarget ? (float?)System.Numerics.Vector2.DistanceSquared(pendingClickPoint, pendingTarget) : null,
                pointerDistanceSquared = currentControlPoint is { } pointerTarget ? (float?)System.Numerics.Vector2.DistanceSquared(Gui.GetIO().MousePos, pointerTarget) : null,
                lastInputFrame,
                mouse = new { x = Gui.GetIO().MousePos.X, y = Gui.GetIO().MousePos.Y },
                selected = Game.Document?.Selected?.Name, selectedId = Game.Document?.Selected?.Id,
                dirty = Game.Document?.IsDirty, fixture = Game.Document?.Path,
                registeredControls = Game.Controls.Centers.Keys.Take(80).ToArray(),
                nativeDockTabs = Game.CaptureEngineDockProgress(),
                engine = Game.EngineDiagnostic, status = Game.StatusMessage,
                lifecycle = Game.CaptureDiagnosticCompletionProgress(), evidence = Evidence,
                recentActions = actions.TakeLast(12).ToArray(),
            }) + Environment.NewLine);
        }
        catch (Exception error) { progressWriteError = error.Message; Console.Error.WriteLine(Scenario + " progress inspection failed: " + error.Message); }
    }
    protected static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    protected virtual void CleanInput() { }
    protected void Finish(bool passed, string result)
    {
        Finished = true; Passed = passed; Result = result;
        WriteProgress(true);
        reportFixture = Game.Document?.Path; reportEvidence = Evidence;
        var io = Gui.GetIO(); io.AddMouseButtonEvent(0, false); io.AddKeyEvent(ImGuiKey.ModCtrl, false);
        io.AddKeyEvent(ImGuiKey.A, false); io.AddKeyEvent(ImGuiKey.Enter, false);
        try { CleanInput(); }
        catch (Exception error) { Passed = false; Result += Environment.NewLine + "Fixture cleanup failed: " + error; }
        WriteReport(false);
    }
    internal virtual void FinalizeCompletion(bool passed, string result)
    { Passed = passed; Result = result; WriteReport(true); }
    private void WriteReport(bool teardownCompleted)
    {
        File.WriteAllText(Path.Combine(Artifacts, Scenario + "-result.json"), JsonSerializer.Serialize(new
        {
            scenario = Scenario, passed = teardownCompleted && Passed, scenarioPassed = Passed, result = Result,
            teardownCompleted, phase = Phase, durationSeconds = Clock.Elapsed.TotalSeconds,
            sharedTerminalTeardownPath = SharedTerminalTeardownPath,
            fixture = reportFixture, evidence = reportEvidence, actions,
            adapter = Game.GraphicsDevice.Adapter.DriverInfo,
            coverage = "Synthetic ImGui input with actual compiled engine resources. Native physical input is not covered.",
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
