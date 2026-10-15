using Hexa.NET.ImGui;
using Stride.Assets.Models;
using Stride.Core.Assets;
using Stride.Rendering.ProceduralModels;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed class EngineRecompileCapabilityProbe : EngineUiCapabilityProbe
{
    private ModelAssetPreview? original;
    private ModelAssetPreview? updatePreCommandPreview;
    private SceneDocument? sceneOwner;
    private EngineViewport? sceneView;
    private object? sceneOwnerCertificate;
    private Guid? sceneEntryId;
    private object? sceneEntryCertificate;
    private string? scenePath, sceneAssetId;
    private Guid[] sceneEntities = [], sceneSelection = [];
    private long sceneBaselineOutputSequence;
    private PreviewLifetimeVerificationOwner? lifetime;
    private Task<string>? compiler;
    private string? injectedFailure;
    private long failureOutputStart;
    private BuildDiagnostic? failureDiagnostic;
    private bool failureVerified;
    private int updatePressFrame = -1;
    private long updateActivationSequence, updateStartSequence;
    private int updatePressAttempts;
    private EngineOutputSample? baseline, last;
    private EngineOutputDifference? changed;
    private int stable;
    private double nextSample;
    private float originalRadius, newRadius;
    private float preManualRuntimeRadius;
    private Stride.Core.Mathematics.Matrix sceneCamera;
    protected override string Scenario => "E04";
    protected override double PhaseTimeoutSeconds => Phase is >= 15 and <= 20 ? 180 : base.PhaseTimeoutSeconds;
    protected override object Evidence => new { originalRadius, newRadius, preManualRuntimeRadius, compiler = compiler?.Status.ToString(), changed,
        outputAttribution = "Scene image delta spans the entire authored-edit/automatic-refresh/manual-retry workflow; it is not attributed solely to the retry.",
        compilerException = compiler?.Exception?.ToString(), failureDiagnostic, failureVerified,
        updateButton = Game.VerificationAssetUpdateButton, updateActivation = Game.VerificationAssetUpdateActivation,
        updateStarted = Game.VerificationAssetUpdateStarted,
        modelPreviewOpenings = Game.VerificationModelPreviewOpenings,
        sceneOwnerCertificate,
        sceneEntryCertificate,
        recentOutput = Game.CaptureWorkflowRecentOutput(512),
        activePreviewSameGeneration = original != null && ReferenceEquals(Game.ModelPreview, original),
        activePreviewName = Game.ModelPreview?.Name, activePreviewDisposed = Game.ModelPreview?.IsDisposed,
        oldGeneration = lifetime?.Snapshot(Game),
        gaps = new[] { "External source reimport, other asset families and full dependency propagation need separate cases." } };
    internal EngineRecompileCapabilityProbe(EditorGame game, string artifacts) : base(game, artifacts) { }
    protected override void CleanInput()
    { if (injectedFailure != null && File.Exists(injectedFailure)) File.Delete(injectedFailure); }
    protected override void Step()
    {
        var io = Gui.GetIO();
        switch (Phase)
        {
            case 0: if (Click("toolbar/open")) Next(); break;
            case 1: if (Click("project/open")) Next(); break;
            case 2:
                if (baseline == null)
                {
                    if (Game.Document == null || !EnsureScenePresented() || !StableSceneOutput(out var scene)) break;
                    sceneOwner = Game.Document;
                    sceneEntryId = Game.VerificationDocumentId(sceneOwner);
                    scenePath = sceneOwner.Path; sceneAssetId = sceneOwner.Asset.Id.ToString();
                    sceneEntities = sceneOwner.Asset.Hierarchy.Parts.Keys.Order().ToArray();
                    sceneSelection = sceneOwner.SelectedIds.ToArray();
                    sceneBaselineOutputSequence = Game.CaptureWorkflowRecentOutput(512).LastOrDefault()?.Sequence ?? 0;
                    sceneView = Game.VerificationDocumentViewport(sceneOwner);
                    Require(sceneView?.RuntimeScene != null, "The baseline scene must own its published real runtime viewport.");
                    baseline = scene;
                    sceneCamera = Game.VerificationSceneCamera;
                    Game.SaveSceneVerificationOutput(Path.Combine(Artifacts, "E04-scene-before.png"));
                }
                if (Click("asset/filter")) Next(); break;
            case 3: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 4:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var c in "Sphere") io.AddInputCharacter(c); Next(); break;
            case 5: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 6: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 7: if (Click("asset/Sphere")) Next(); break;
            case 8:
                if (Game.ModelPreview is not { RenderCount: > 0 } preview) break;
                if (Click("asset/edit")) Next(); break;
            case 9:
                if (Game.AssetDocument?.Asset.Type is not SphereProceduralModel sphere) break;
                if (Game.ModelPreview is not { RenderCount: > 0 } editorPreview) break;
                originalRadius = sphere.Radius; newRadius = originalRadius * 2.5f;
                if (original == null)
                {
                    Require(editorPreview.RenderError == null && Math.Abs(editorPreview.ModelSize.X - originalRadius * 2) < .001f,
                        "The canonical dedicated editor must retain the actual original compiled model geometry before edits.");
                    original = editorPreview; lifetime = Game.CapturePreviewLifetimeVerification(true);
                }
                if (Click("asset-editor/field/Radius")) Next(); break;
            case 10: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 11:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var c in newRadius.ToString("R", System.Globalization.CultureInfo.InvariantCulture)) io.AddInputCharacter(c); Next(); break;
            case 12: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 13: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 14:
                if (updatePressFrame >= 0) { if (ClickVerifiedUpdate()) Next(); break; }
                if (Game.AssetDocument?.Asset.Type is not SphereProceduralModel edited || edited.Radius != newRadius) break;
                Require(Game.AssetDocument.IsDirty && Game.AssetDocument.CanUndo, "Radius UI edit must produce real authored history.");
                if (injectedFailure == null)
                {
                    failureOutputStart = Game.CaptureWorkflowRecentOutput(512).LastOrDefault()?.Sequence ?? 0;
                    injectedFailure = Game.CreateAssetCompilationFailureVerification();
                }
                if (ClickVerifiedUpdate()) Next(); break;
            case 15:
                compiler ??= Game.VerificationStartedAssetUpdateTask;
                if (compiler != null) Next(); break;
            case 16:
                if (!compiler!.IsCompleted || Game.IsAssetPreviewUpdating) break;
                if (!failureVerified)
                {
                Require(compiler.IsFaulted && ReferenceEquals(Game.ModelPreview, original) && !original!.IsDisposed,
                    "The actual compiler failure must preserve the prior real preview generation.");
                failureDiagnostic = Game.CaptureWorkflowRecentOutput(512)
                    .Where(entry => entry.Sequence > failureOutputStart).Select(entry => entry.Diagnostic)
                    .FirstOrDefault(diagnostic => diagnostic is { Code: "CS1029", Line: 1 }
                        && !string.IsNullOrWhiteSpace(diagnostic.Path) && Path.IsPathFullyQualified(diagnostic.Path)
                        && string.Equals(Path.GetFullPath(diagnostic.Path), Path.GetFullPath(injectedFailure!), StringComparison.OrdinalIgnoreCase)
                        && diagnostic.Message.Contains("Intentional E04 disposable compilation failure", StringComparison.Ordinal));
                Require(failureDiagnostic != null,
                    "The faulted real compiler must report CS1029 at the exact injected disposable source path, line and marker.");
                Require(Math.Abs(OwnedSceneRadius(original!.Name) - preManualRuntimeRadius) < .0001f,
                    "Failed compilation must preserve the scene's original compiled model.");
                File.Delete(injectedFailure!); injectedFailure = null;
                failureVerified = true;
                }
                if (ClickVerifiedUpdate()) Next(); break;
            case 17:
                compiler = Game.VerificationStartedAssetUpdateTask;
                if (compiler != null) Next(); break;
            case 18:
                if (!compiler!.IsCompleted || Game.IsAssetPreviewUpdating || Game.ModelPreview is not { RenderCount: > 0 } current) break;
                Require(compiler.IsCompletedSuccessfully && !ReferenceEquals(current, original) && current.SourceContent != original!.SourceContent,
                    "Successful native compilation must install a distinct compiled content generation.");
                Require(current.RenderError == null && current.MeshCount > 0 && current.MaterialPassCount > 0
                    && Math.Abs(current.ModelSize.X - newRadius * 2) < .001f,
                    "The new real preview GPU model must have the edited geometric bounds.");
                Require(Math.Abs(OwnedSceneRadius(current.Name) - newRadius) < .0001f,
                    "Native scene consumers must receive the new compiled geometry.");
                var persisted = AssetFileSerializer.Load<ProceduralModelAsset>(Game.AssetDocument!.Path).Asset;
                Require(persisted.Type is SphereProceduralModel saved && saved.Radius == newRadius,
                    "The compiler must consume the independently reloaded saved asset.");
                last = null; stable = 0; Next(); break;
            case 19:
                if (!EnsureScenePresented()) break;
                Next(); break;
            case 20:
                if (!StableSceneOutput(out var after)) break;
                Require(Game.VerificationSceneCamera == sceneCamera, "Recompilation must retain the actual scene camera for meaningful output comparison.");
                changed = baseline!.Compare(after, 0, 0, baseline.Width, baseline.Height);
                EngineCapabilityAssertions.VisibleFixtureChange(changed.Value, Math.Max(16, changed.Value.Pixels / 1000), .99);
                Game.SaveSceneVerificationOutput(Path.Combine(Artifacts, "E04-scene-recompiled.png"));
                if (!lifetime!.IsSettled(Game)) break;
                Finish(true, "E04 PASS: actual compiler failure preserves old compiled data; retry installs saved procedural geometry, updates the real scene GPU output and retires the previous preview/cache/target generation.");
                break;
        }
    }
    private float OwnedSceneRadius(string url)
    {
        sceneOwnerCertificate = Game.InspectEngineSceneOwnerCertificate(sceneOwner, sceneView);
        sceneEntryCertificate = Game.InspectEngineSceneEntryCertificate(sceneEntryId, sceneOwner, sceneView);
        var current = scenePath == null || sceneAssetId == null ? null
            : Game.ResolveEngineSceneEntry(sceneEntryId, scenePath, sceneAssetId);
        Require(current != null && sceneView?.RuntimeScene != null
            && ReferenceEquals(Game.VerificationDocumentViewport(current), sceneView),
            "Recompilation must retain the exact logical scene entry and its current published runtime viewport.");
        if (!ReferenceEquals(current, sceneOwner))
            Require(Game.CaptureWorkflowRecentOutput(512).Any(entry => entry.Sequence > sceneBaselineOutputSequence
                && entry.Module == "Reload assemblies" && entry.Text == "Assemblies reloaded"),
                "A changed scene generation requires evidence of the actual native assembly reload.");
        Require(sceneEntities.SequenceEqual(current!.Asset.Hierarchy.Parts.Keys.Order())
            && sceneSelection.SequenceEqual(current.SelectedIds),
            "Canonical scene generation changes must preserve authored entity and selection identities.");
        Require(sceneView!.Camera.Component.ViewMatrix == sceneCamera,
            "Canonical scene generation changes must preserve the actual baseline camera.");
        return sceneView!.RuntimeModelHalfExtent(url);
    }
    private bool ClickVerifiedUpdate()
    {
        if (updatePressFrame >= 0)
        {
            var activated = Game.VerificationAssetUpdateActivationSequence > updateActivationSequence;
            var started = Game.VerificationAssetUpdateStartSequence > updateStartSequence;
            if (activated && started)
            {
                Require(ReferenceEquals(Game.VerificationAssetUpdateActivatedPreview, updatePreCommandPreview),
                    "Actual update activation must refer to the certified pre-command preview generation.");
                compiler = Game.VerificationStartedAssetUpdateTask
                    ?? throw new InvalidOperationException("An actual preview-start certificate must retain its task.");
                updatePressFrame = -1; updatePressAttempts = 0;
                return true;
            }
            // Ordinary buttons activate on release. Allow that event to reach
            // the actual draw before deciding whether the press was delivered.
            if (Gui.GetFrameCount() - updatePressFrame < 4) return false;
            Require(!started, "A preview task started without this probe's actual button activation certificate.");
            Require(!activated, "The update button activated but did not start the real preview task.");
            Require(updatePressAttempts < 3, "The real update button did not activate after three bounded UI presses.");
            Require(!Game.IsAssetPreviewUpdating, "Do not retry a UI press while any preview compilation is running.");
            updatePressFrame = -1;
        }
        var activation = Game.VerificationAssetUpdateActivationSequence;
        var start = Game.VerificationAssetUpdateStartSequence;
        if (Game.VerificationAssetUpdatePipelineBusy) return false;
        var preCommand = Game.ModelPreview;
        Require(preCommand is { IsDisposed: false, RenderCount: > 0 } && preCommand.RenderError == null
            && preCommand.SourceContent != null && preCommand.MeshCount > 0,
            "The real pre-command preview must be compiled, rendered and owned before an update press.");
        if (!failureVerified)
        {
            var beforeCommand = scenePath == null || sceneAssetId == null ? null
                : Game.ResolveEngineSceneEntry(sceneEntryId, scenePath, sceneAssetId);
            Require(beforeCommand != null, "The pre-command scene entry must be current and published.");
            sceneSelection = beforeCommand!.SelectedIds.ToArray();
            preManualRuntimeRadius = OwnedSceneRadius(preCommand!.Name);
            Require(float.IsFinite(preManualRuntimeRadius)
                && Math.Abs(preCommand.ModelSize.X - preManualRuntimeRadius * 2) < .001f,
                "The pre-command preview and actual scene consumer must agree on compiled geometry.");
            original = preCommand; lifetime = Game.CapturePreviewLifetimeVerification(true);
        }
        if (!Click("asset-editor/update")) return false;
        updatePreCommandPreview = preCommand;
        updateActivationSequence = activation; updateStartSequence = start;
        updatePressFrame = Gui.GetFrameCount(); updatePressAttempts++;
        return false;
    }
    private bool StableSceneOutput(out EngineOutputSample sample)
    {
        sample = null!;
        if (Clock.Elapsed.TotalSeconds < nextSample) return false;
        nextSample = Clock.Elapsed.TotalSeconds + .25;
        sample = Game.ReadSceneVerificationOutput();
        var same = last != null && last.Width == sample.Width && last.Height == sample.Height && last.Format == sample.Format
            && last.Compare(sample, 0, 0, sample.Width, sample.Height).ChangedFraction <= .001;
        last = sample; stable = same ? stable + 1 : 0; return stable >= 3;
    }
}
