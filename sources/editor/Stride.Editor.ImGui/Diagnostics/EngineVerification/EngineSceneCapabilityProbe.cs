using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

/// <summary>E01: real compiled scene, UI transform edit, GPU output change and undo restoration.</summary>
internal sealed class EngineSceneCapabilityProbe
{
    private readonly EditorGame game;
    private readonly string artifacts;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly List<string> actions = [];
    private SceneDocument? owner;
    private Guid entityId;
    private Guid? parentId;
    private float originalX, changedX;
    private Matrix camera;
    private EngineOutputSample? baseline, previousSample;
    private EngineOutputDifference? changedOutput, restoredOutput;
    private int phase, stableSamples;
    private bool releaseMouse;
    private int dismissCameraPopup;
    private string? pendingClickId;
    private System.Numerics.Vector2 pendingClickPoint;
    private string? lastMissingControl, progressWriteError;
    private double nextProgress;
    private int lastInputFrame = -1;
    private double phaseStart, nextSample;
    private long phaseRenderRevision, sampledRenderRevision;
    internal bool Finished { get; private set; }
    internal bool Passed { get; private set; }
    internal string Result { get; private set; } = "";

    internal EngineSceneCapabilityProbe(EditorGame game, string artifactDirectory)
    {
        this.game = game;
        artifacts = Path.GetFullPath(artifactDirectory);
        Directory.CreateDirectory(artifacts);
    }

    // Host invokes this after the tested scene draw, once per frame, on its owning thread.
    internal void Update()
    {
        if (Finished) return;
        WriteProgress();
        try
        {
            if (clock.Elapsed.TotalSeconds - phaseStart > (phase <= 2 ? 180 : 45))
                throw new TimeoutException($"E01 phase {phase} timed out: {game.StatusMessage}; {game.EngineDiagnostic}");
            var inputFrame = Gui.GetFrameCount();
            if (inputFrame == lastInputFrame) return;
            lastInputFrame = inputFrame;
            var io = Gui.GetIO(); io.AddFocusEvent(true);
            if (releaseMouse) { io.AddMouseButtonEvent(0, false); releaseMouse = false; return; }
            if (dismissCameraPopup != 0)
            {
                io.AddKeyEvent(ImGuiKey.Escape, dismissCameraPopup == 2);
                dismissCameraPopup--; return;
            }
            switch (phase)
            {
                case 0: if (Click("toolbar/open")) Next(); break;
                case 1: if (Click("project/open")) Next(); break;
                case 2:
                    if (game.Document == null || !game.VerificationSceneHasRuntime) break;
                    if (!game.VerificationScenePresented)
                    {
                        if (game.RecordVerificationSceneDockTab()) Click("engine/scene-tab");
                        break;
                    }
                    game.AssertCompiledSceneVerification();
                    if (Click("entity/Imported cube")) Next();
                    break;
                case 3:
                    if (game.Document?.Selected?.Name != "Imported cube") break;
                    owner = game.Document;
                    Require(!owner.IsDirty && owner.CanEditSelected, "Disposable fixture must begin clean and editable.");
                    entityId = owner.Selected!.Id; parentId = owner.Selected.Transform.Parent?.Entity.Id;
                    originalX = owner.Selected.Transform.Position.X; changedX = originalX + 10f;
                    Require(float.IsFinite(originalX), "Fixture transform is not finite.");
                    if (Click("camera/focus")) Next();
                    break;
                case 4:
                    if (!EnsureFreshSceneOutput()) break;
                    RequireOwner(); camera = game.VerificationSceneCamera;
                    game.AssertRuntimeEntityVerification(entityId, parentId);
                    if (!StableOutput(out var initial)) break;
                    baseline = initial;
                    game.SaveSceneVerificationOutput(Path.Combine(artifacts, "E01-baseline.png"));
                    if (Click("Position/0")) Next();
                    break;
                case 5: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
                case 6:
                    io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                    foreach (var c in changedX.ToString("R", CultureInfo.InvariantCulture)) io.AddInputCharacter(c);
                    Next(); break;
                case 7: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
                case 8: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
                case 9:
                    RequireOwner();
                    if (owner!.Selected!.Transform.Position.X != changedX) break;
                    Require(owner.IsDirty && owner.CanUndo, "UI transform edit must create undoable authored data.");
                    game.AssertRuntimeEntityVerification(entityId, parentId);
                    if (!EnsureFreshSceneOutput()) break;
                    Require(game.VerificationSceneCamera == camera, "Editing a transform changed the independent editor camera.");
                    if (!StableOutput(out var changed)) break;
                    changedOutput = baseline!.Compare(changed, 0, 0, baseline.Width, baseline.Height);
                    EngineCapabilityAssertions.VisibleFixtureChange(changedOutput.Value,
                        Math.Max(16, changedOutput.Value.Pixels / 1000), 0.99);
                    game.SaveSceneVerificationOutput(Path.Combine(artifacts, "E01-transformed.png"));
                    if (Click("toolbar/undo")) Next();
                    break;
                case 10:
                    RequireOwner();
                    if (owner!.Selected!.Transform.Position.X != originalX) break;
                    Require(!owner.IsDirty && owner.CanRedo, "Undo must restore the clean fixture and offer redo.");
                    game.AssertRuntimeEntityVerification(entityId, parentId);
                    if (!EnsureFreshSceneOutput()) break;
                    Require(game.VerificationSceneCamera == camera, "Undo changed the independent editor camera.");
                    if (!StableOutput(out var restored)) break;
                    restoredOutput = baseline!.Compare(restored, 0, 0, baseline.Width, baseline.Height);
                    if (restoredOutput.Value.ChangedFraction > 0.005) break;
                    game.SaveSceneVerificationOutput(Path.Combine(artifacts, "E01-restored.png"));
                    Finish(true, "E01 PASS: compiled fixture resources, UI transform, runtime identity/world transform, actual GPU output change and undo restoration.");
                    break;
            }
        }
        catch (Exception error)
        {
            try { game.SaveSceneVerificationOutput(Path.Combine(artifacts, "E01-failure.png")); }
            catch (Exception captureError) { actions.Add("Failure capture: " + captureError.Message); }
            Finish(false, error.ToString());
        }
    }

    private bool StableOutput(out EngineOutputSample sample)
    {
        sample = null!;
        if (clock.Elapsed.TotalSeconds < nextSample) return false;
        if (game.VerificationSceneRenderRevision <= sampledRenderRevision) return false;
        sampledRenderRevision = game.VerificationSceneRenderRevision;
        nextSample = clock.Elapsed.TotalSeconds + 0.25;
        sample = game.ReadSceneVerificationOutput();
        var stable = previousSample != null && previousSample.Width == sample.Width && previousSample.Height == sample.Height
            && previousSample.Format == sample.Format
            && previousSample.Compare(sample, 0, 0, sample.Width, sample.Height).ChangedFraction <= 0.001;
        previousSample = sample;
        stableSamples = stable ? stableSamples + 1 : 0;
        return stableSamples >= 3;
    }

    private void RequireOwner()
    {
        Require(ReferenceEquals(game.Document, owner) && owner?.Selected?.Id == entityId,
            "The edit must retain the exact original document and selected entity.");
    }

    private bool EnsureFreshSceneOutput()
    {
        if (!game.VerificationScenePresented)
        {
            stableSamples = 0; previousSample = null;
            phaseRenderRevision = game.VerificationSceneRenderRevision;
            if (game.RecordVerificationSceneDockTab()) Click("engine/scene-tab");
            return false;
        }
        return game.VerificationSceneRenderRevision > phaseRenderRevision;
    }

    private bool Click(string id)
    {
        if (!game.Controls.Centers.TryGetValue(id, out var point))
        {
            lastMissingControl = id;
            var tab = id.StartsWith("Position/", StringComparison.Ordinal) ? "tooltab/editor.properties"
                : id.StartsWith("entity/", StringComparison.Ordinal) ? "tooltab/scene.hierarchy"
                : id is "camera/focus" or "camera/reset" ? "camera/lens" : null;
            if (tab != null) Click(tab); else pendingClickId = null;
            lastMissingControl = id;
            return false;
        }
        lastMissingControl = null;
        var io = Gui.GetIO();
        if (pendingClickId != id || pendingClickPoint != point || System.Numerics.Vector2.DistanceSquared(io.MousePos, point) > 2f)
        {
            pendingClickId = id; pendingClickPoint = point; io.AddMousePosEvent(point.X, point.Y);
            actions.Add($"{clock.Elapsed.TotalSeconds:F3}: aim {id} ({point.X:F1},{point.Y:F1})"); return false;
        }
        pendingClickId = null; io.AddMouseButtonEvent(0, true);
        if (id is "camera/focus" or "camera/reset") dismissCameraPopup = 2;
        releaseMouse = true; actions.Add($"{clock.Elapsed.TotalSeconds:F3}: click {id}"); return true;
    }

    private void Next()
    { phase++; phaseStart = clock.Elapsed.TotalSeconds; stableSamples = 0; previousSample = null;
        phaseRenderRevision = sampledRenderRevision = game.VerificationSceneRenderRevision; WriteProgress(true); }

    private void WriteProgress(bool force = false)
    {
        if (!force && clock.Elapsed.TotalSeconds < nextProgress) return;
        nextProgress = clock.Elapsed.TotalSeconds + 2;
        try
        {
            File.AppendAllText(Path.Combine(artifacts, "E01-progress.jsonl"), JsonSerializer.Serialize(new
            {
                schema = 1, utc = DateTimeOffset.UtcNow, process = Environment.ProcessId, phase,
                phaseSeconds = clock.Elapsed.TotalSeconds - phaseStart, elapsedSeconds = clock.Elapsed.TotalSeconds,
                lastMissingControl, pendingClickId, releaseMouse, dismissCameraPopup, stableSamples, progressWriteError,
                pendingClickPoint = new { x = pendingClickPoint.X, y = pendingClickPoint.Y },
                mouse = new { x = Gui.GetIO().MousePos.X, y = Gui.GetIO().MousePos.Y },
                selected = game.Document?.Selected?.Name, selectedId = game.Document?.Selected?.Id,
                dirty = game.Document?.IsDirty, fixture = game.Document?.Path,
                registeredControls = game.Controls.Centers.Keys.Take(80).ToArray(),
                engine = game.EngineDiagnostic, status = game.StatusMessage,
                lifecycle = game.CaptureDiagnosticCompletionProgress(),
                recentActions = actions.TakeLast(12).ToArray(),
            }) + Environment.NewLine);
        }
        catch (Exception error) { progressWriteError = error.Message; Console.Error.WriteLine("E01 progress inspection failed: " + error.Message); }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("E01: " + message); }

    private void Finish(bool passed, string result)
    {
        Passed = passed; Finished = true; Result = result;
        WriteProgress(true);
        var io = Gui.GetIO(); io.AddMouseButtonEvent(0, false);
        io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false); io.AddKeyEvent(ImGuiKey.Enter, false);
        WriteReport(false);
    }

    internal void FinalizeCompletion(bool passed, string result)
    { Passed = passed; Result = result; WriteReport(true); }

    private void WriteReport(bool teardownCompleted)
    {
        File.WriteAllText(Path.Combine(artifacts, "E01-result.json"), JsonSerializer.Serialize(new
        {
            scenario = "E01", passed = teardownCompleted && Passed, scenarioPassed = Passed, result = Result,
            teardownCompleted, phase, durationSeconds = clock.Elapsed.TotalSeconds,
            fixture = owner?.Path, entityId, parentId, originalX, changedX, changedOutput, restoredOutput,
            adapter = game.GraphicsDevice.Adapter.DriverInfo, engine = game.EngineDiagnostic, actions,
            coverage = "Synthetic ImGui input, real compiled runtime and synchronous scene GPU readback. No native physical input or WPF pixel comparison.",
            tolerance = new { channel = 3, stabilityFraction = 0.001, restoredFraction = 0.005,
                minimumChangedFraction = 0.001, maximumChangedFraction = 0.99, needsBaselineReview = true },
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
