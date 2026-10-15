using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record AnimationControlsSliderReceipt(uint ItemId, uint ViewportId, int Frame,
        System.Numerics.Vector2 Minimum, System.Numerics.Vector2 Maximum, bool Visible);
    private AnimationControlsSliderReceipt? animationControlsSliderReceipt;
    private unsafe void RecordAnimationPreviewControlsSliderItem()
    {
        if (!Controls.Enabled) return;
        var window = ImGuiP.GetCurrentWindowRead();
        var minimum = Gui.GetItemRectMin(); var maximum = Gui.GetItemRectMax();
        if (!window.IsNull)
        {
            minimum = System.Numerics.Vector2.Max(minimum, window.ClipRect.Min);
            maximum = System.Numerics.Vector2.Min(maximum, window.ClipRect.Max);
        }
        animationControlsSliderReceipt = new(Gui.GetItemID(), Gui.GetWindowViewport().ID, Gui.GetFrameCount(),
            minimum, maximum, maximum.X > minimum.X && maximum.Y > minimum.Y);
    }
    private AnimationPreviewControlsRun? animationPreviewControls;
    internal object? AnimationPreviewControlsEvidence => animationPreviewControls?.Inspection;
    internal bool StepAnimationPreviewControls(Func<string, bool> click, string assetPath)
    {
        if (!Controls.Enabled || !args.Contains("--ui-test")) throw new InvalidOperationException("Animation controls require the isolated synthetic native fixture.");
        animationPreviewControls ??= new(this, assetPath);
        return animationPreviewControls.Step(click);
    }
    internal void CleanAnimationPreviewControlsInput()
    {
        var io = Gui.GetIO();
        io.AddMouseButtonEvent(0, false); io.AddKeyEvent(ImGuiKey.ModCtrl, false);
        io.AddKeyEvent(ImGuiKey.A, false); io.AddKeyEvent(ImGuiKey.Enter, false);
    }

    private sealed class AnimationPreviewControlsRun
    {
        private readonly EditorGame game;
        private readonly AnimationAssetPreview preview;
        private readonly ModelAssetPreview view;
        private readonly PreviewFamilyLifetimeVerification lifetime;
        private readonly EditorDocumentEntry scene;
        private readonly EditorAuthoringState state;
        private readonly DocumentGroupParityView sceneBaseline;
        private readonly string assetPath;
        private readonly byte[] sceneBytes, assetBytes;
        private readonly Guid[] transactionIds;
        private readonly int historyPosition;
        private readonly float originalSpeed;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly List<object> receipts = [];
        private int phase, pressFrame, releaseFrame, heldFrames, zeroRenderCount;
        private double zeroTime, zeroAt, initialTime, scrubTime;
        private EngineOutputSample? originalOutput, scrubOutput, zeroOutput;
        private EngineOutputDifference? scrubDifference, speedDifference;

        internal AnimationPreviewControlsRun(EditorGame game, string assetPath)
        {
            this.game = game; this.assetPath = Path.GetFullPath(assetPath);
            preview = game.animationPreview ?? throw new InvalidOperationException("Actual compiled animation preview is missing.");
            view = ReadFamilyField(preview, "view") as ModelAssetPreview ?? throw new InvalidOperationException("Native animation renderer is missing.");
            scene = game.documents.Active ?? throw new InvalidOperationException("Original scene must remain active.");
            state = scene.State ?? throw new InvalidOperationException("Original scene canonical generation is missing.");
            sceneBaseline = game.InspectDocumentGroupParity(scene.Id) ?? throw new InvalidOperationException("Original scene pane is missing.");
            if (scene.Document is not EntityHierarchyDocument || !sceneBaseline.Visible || !preview.Visible || preview.IsPlaying
                || !string.Equals(game.assetPreviewPath, this.assetPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Start after visibility verification paused the exact native animation beside its original scene.");
            originalSpeed = preview.TimeScale;
            if (originalSpeed != 1) throw new InvalidOperationException("Native isolated animation speed must start at one.");
            sceneBytes = File.ReadAllBytes(scene.Path); assetBytes = File.ReadAllBytes(this.assetPath);
            historyPosition = game.sessionHistory!.History.Position;
            transactionIds = game.sessionHistory.Stack.RetrieveAllTransactions().Select(transaction => transaction.Id).ToArray();
            lifetime = game.CaptureFamilyLifetime("animation");
            initialTime = preview.CurrentSeconds; originalOutput = game.ReadFamilyOutput("animation");
            Write("baseline");
        }
        private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        internal object Inspection
        {
            get
            {
                var currentScene = game.InspectDocumentGroupParity(scene.Id);
                var roots = lifetime.Cache?.GetLoadedAssetsSnapshot().Cast<object>().ToArray() ?? [];
                var actualTransactions = game.sessionHistory!.Stack.RetrieveAllTransactions().Select(transaction => transaction.Id).ToArray();
                return new
                {
                    phase, seconds = clock.Elapsed.TotalSeconds, assetPath, pressFrame, releaseFrame, heldFrames,
                    initialTime, scrubTime, zeroTime, zeroAt, zeroRenderCount, originalSpeed, preview.TimeScale,
                    preview.CurrentSeconds, preview.DurationSeconds, preview.IsPlaying, preview.Visible,
                    previewRenderCount = view.RenderCount, previewDrawCalls = view.DrawCalls,
                    nativeLeftDown = Gui.IsMouseDown(ImGuiMouseButton.Left), Gui.GetCurrentContext().ActiveId,
                    scrubOwnerSame = ReferenceEquals(game.animationScrubOwner, preview), game.animationScrubFrame,
                    nativeFrame = Gui.GetFrameCount(), ownerSame = ReferenceEquals(game.animationPreview, preview),
                    actualSlider = game.animationControlsSliderReceipt == null ? null : new
                    {
                        game.animationControlsSliderReceipt.ItemId, game.animationControlsSliderReceipt.ViewportId,
                        game.animationControlsSliderReceipt.Frame, game.animationControlsSliderReceipt.Visible,
                        minimum = new { game.animationControlsSliderReceipt.Minimum.X, game.animationControlsSliderReceipt.Minimum.Y },
                        maximum = new { game.animationControlsSliderReceipt.Maximum.X, game.animationControlsSliderReceipt.Maximum.Y },
                    },
                    nativeHoveredId = Gui.GetCurrentContext().HoveredId,
                    mouse = new { Gui.GetIO().MousePos.X, Gui.GetIO().MousePos.Y },
                    nativeViewDisposed = view.IsDisposed, targetDisposed = lifetime.Targets.IsDisposed,
                    targetRegistered = game.renderer!.HasVerificationTexture(lifetime.Reference),
                    targetRetirementQueued = game.IsVerificationRetirementOwnerQueued(lifetime.Targets),
                    nativeCacheRootsRetained = lifetime.Roots.All(root => roots.Any(current => ReferenceEquals(current, root))),
                    sceneStateSame = ReferenceEquals(scene.State, state), state.IsPublished, state.IsDisposed,
                    expectedSceneGeneration = state.Generation, actualSceneGeneration = scene.State?.Generation,
                    sceneRuntimeSame = ReferenceEquals(currentScene?.Runtime, sceneBaseline.Runtime),
                    sceneCameraSame = currentScene?.Camera == sceneBaseline.Camera, sceneVisible = currentScene?.Visible,
                    originalSceneRenderRevision = sceneBaseline.RenderRevision, actualSceneRenderRevision = currentScene?.RenderRevision,
                    expectedHistoryPosition = historyPosition, actualHistoryPosition = game.sessionHistory.History.Position,
                    expectedTransactionIds = transactionIds, actualTransactionIds = actualTransactions,
                    historyPositionSame = historyPosition == game.sessionHistory.History.Position,
                    allTransactionIdsSame = transactionIds.SequenceEqual(actualTransactions),
                    selectedAssetSame = string.Equals(game.selectedAssetPath, assetPath, StringComparison.OrdinalIgnoreCase),
                    sceneBytesSame = File.ReadAllBytes(scene.Path).AsSpan().SequenceEqual(sceneBytes),
                    assetBytesSame = File.ReadAllBytes(assetPath).AsSpan().SequenceEqual(assetBytes),
                    expectedSceneSha = Sha(sceneBytes), actualSceneSha = Sha(File.ReadAllBytes(scene.Path)),
                    expectedAssetSha = Sha(assetBytes), actualAssetSha = Sha(File.ReadAllBytes(assetPath)),
                    scrubDifference, speedDifference, receipts,
                    coverage = "Actual held native slider scrub and text numeric speed controls on the existing compiled FBX animation; pause/resume intent, zero-speed time isolation, real GPU changes and exact authored owner/history/files. Synthetic native IO only. No all-clip, physical input or tick-artwork claim. Family/outer retirement required.",
                };
            }
        }
        private void Write(string reason)
        {
            var root = Environment.GetEnvironmentVariable("STRIDE_EDITOR_TEST_ARTIFACTS") ?? throw new InvalidOperationException("Owned animation control artifact root is missing.");
            File.WriteAllText(Path.Combine(root, "animation-preview-controls-" + reason + ".json"), JsonSerializer.Serialize(Inspection));
        }
        private void Require(bool condition, string message)
        { if (!condition) { Write("failure"); throw new InvalidOperationException(message); } }
        private void RequireOwners()
        {
            var currentScene = game.InspectDocumentGroupParity(scene.Id);
            var roots = lifetime.Cache?.GetLoadedAssetsSnapshot().Cast<object>().ToArray() ?? [];
            Require(ReferenceEquals(game.animationPreview, preview) && !view.IsDisposed && !lifetime.Targets.IsDisposed
                && game.renderer!.HasVerificationTexture(lifetime.Reference) && !game.IsVerificationRetirementOwnerQueued(lifetime.Targets)
                && lifetime.Roots.All(root => roots.Any(current => ReferenceEquals(root, current)))
                && ReferenceEquals(scene.State, state) && state.IsPublished && !state.IsDisposed
                && ReferenceEquals(currentScene?.Runtime, sceneBaseline.Runtime) && currentScene?.Camera == sceneBaseline.Camera
                && string.Equals(game.selectedAssetPath, assetPath, StringComparison.OrdinalIgnoreCase)
                && game.sessionHistory!.History.Position == historyPosition
                && transactionIds.SequenceEqual(game.sessionHistory.Stack.RetrieveAllTransactions().Select(transaction => transaction.Id))
                && File.ReadAllBytes(scene.Path).AsSpan().SequenceEqual(sceneBytes) && File.ReadAllBytes(assetPath).AsSpan().SequenceEqual(assetBytes),
                "Animation controls must retain exact native cache/target/canonical scene, history including redo and authored files.");
        }
        internal bool Step(Func<string, bool> click)
        {
            Require(clock.Elapsed.TotalSeconds < 60, "Animation scrub/speed exceeded its bounded native input deadline.");
            RequireOwners();
            var io = Gui.GetIO();
            switch (phase)
            {
                case 0: if (click("animation-preview/play")) phase = 1; break;
                case 1:
                    if (!preview.IsPlaying) break;
                    if (game.animationControlsSliderReceipt is not { Visible: true } slider
                        || slider.Frame != Gui.GetFrameCount()) break;
                    var point = (slider.Minimum + slider.Maximum) * .5f;
                    io.AddMouseViewportEvent(slider.ViewportId); io.AddMousePosEvent(point.X, point.Y);
                    if (System.Numerics.Vector2.DistanceSquared(io.MousePos, point) > 2 || Gui.GetCurrentContext().HoveredId != slider.ItemId) break;
                    pressFrame = Gui.GetFrameCount(); io.AddMouseButtonEvent(0, true); phase = 2; break;
                case 2:
                    if (!Gui.IsMouseDown(ImGuiMouseButton.Left)) { Require(Gui.GetFrameCount() <= pressFrame + 8, "Native scrub press was not processed."); break; }
                    Require(ReferenceEquals(game.animationScrubOwner, preview) && !preview.IsPlaying,
                        "Actual native slider capture must pause the playing animation during held scrubbing.");
                    scrubTime = preview.CurrentSeconds; scrubOutput = game.ReadFamilyOutput("animation"); heldFrames = Gui.GetFrameCount(); phase = 3; break;
                case 3:
                    Require(Gui.IsMouseDown(ImGuiMouseButton.Left) && ReferenceEquals(game.animationScrubOwner, preview) && !preview.IsPlaying
                        && preview.CurrentSeconds == scrubTime, "Held native scrub must retain pause intent and the exact sampled time.");
                    if (Gui.GetFrameCount() <= heldFrames + 2) break;
                    scrubDifference = originalOutput!.Compare(scrubOutput!, 0, 0, originalOutput.Width, originalOutput.Height);
                    Require(scrubDifference.Value.ChangedPixels >= 16 && Math.Abs(scrubTime - initialTime) > .001,
                        "Actual native scrub must select a distinct clip sample and change its real GPU output.");
                    releaseFrame = Gui.GetFrameCount(); io.AddMouseButtonEvent(0, false); phase = 4; break;
                case 4:
                    if (Gui.IsMouseDown(ImGuiMouseButton.Left) || Gui.GetFrameCount() <= releaseFrame) break;
                    Require(game.animationScrubOwner == null && preview.IsPlaying,
                        "Native slider release must restore the original playing intent.");
                    receipts.Add(new { contract = "actual-held-native-scrub-pause-and-release-resume", initialTime, scrubTime, pressFrame, releaseFrame, difference = scrubDifference });
                    phase = 5; break;
                case 5: if (click("animation-preview/time-scale")) phase = 6; break;
                case 6: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); phase = 7; break;
                case 7: io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false); phase = 8; break;
                case 8: io.AddInputCharacter('0'); phase = 9; break;
                case 9: io.AddKeyEvent(ImGuiKey.Enter, true); phase = 10; break;
                case 10: io.AddKeyEvent(ImGuiKey.Enter, false); phase = 11; break;
                case 11:
                    if (preview.TimeScale != 0) break;
                    Require(preview.IsPlaying, "Zero speed must preserve native playing intent.");
                    zeroTime = preview.CurrentSeconds; zeroRenderCount = view.RenderCount; zeroAt = clock.Elapsed.TotalSeconds;
                    zeroOutput = game.ReadFamilyOutput("animation"); phase = 12; break;
                case 12:
                    Require(preview.TimeScale == 0 && preview.IsPlaying && preview.CurrentSeconds == zeroTime,
                        "Actual native zero speed must freeze the clip without changing playing intent.");
                    if (clock.Elapsed.TotalSeconds - zeroAt < .2 || view.RenderCount <= zeroRenderCount) break;
                    Require(view.Visible && game.InspectDocumentGroupParity(scene.Id) is { Visible: true } actualScene
                        && actualScene.RenderRevision > sceneBaseline.RenderRevision,
                        "Zero speed must retain actual animation and independent scene rendering.");
                    receipts.Add(new { contract = "actual-native-numeric-zero-speed", zeroTime, zeroRenderCount, actualRenderCount = view.RenderCount });
                    phase = 13; break;
                case 13: if (click("animation-preview/time-scale")) phase = 14; break;
                case 14: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); phase = 15; break;
                case 15: io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false); phase = 16; break;
                case 16: io.AddInputCharacter('1'); phase = 17; break;
                case 17: io.AddKeyEvent(ImGuiKey.Enter, true); phase = 18; break;
                case 18: io.AddKeyEvent(ImGuiKey.Enter, false); phase = 19; break;
                case 19:
                    if (preview.TimeScale != originalSpeed || preview.CurrentSeconds == zeroTime) break;
                    speedDifference = zeroOutput!.Compare(game.ReadFamilyOutput("animation"), 0, 0, zeroOutput.Width, zeroOutput.Height);
                    if (speedDifference.Value.ChangedPixels < 16) break;
                    if (click("animation-preview/pause")) phase = 20; break;
                case 20:
                    if (preview.IsPlaying) break;
                    Require(preview.TimeScale == originalSpeed && game.animationScrubOwner == null && !Gui.IsMouseDown(ImGuiMouseButton.Left),
                        "Control workflow must finish paused with its exact original speed and no native scrub capture.");
                    receipts.Add(new { contract = "actual-numeric-original-speed-restored-and-native-pause", preview.TimeScale, preview.CurrentSeconds, difference = speedDifference });
                    phase = 21; Write("passed"); return true;
                case 21: return true;
            }
            return false;
        }
    }
}
