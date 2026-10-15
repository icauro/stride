using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private AnimationPreviewVisibilityRun? animationPreviewVisibility;
    internal object? AnimationPreviewVisibilityEvidence => animationPreviewVisibility?.Inspection;
    internal bool StepAnimationPreviewVisibility(Func<string, bool> click, string assetPath)
    {
        if (!Controls.Enabled || !args.Contains("--ui-test"))
            throw new InvalidOperationException("Animation visibility verification requires the isolated native fixture.");
        animationPreviewVisibility ??= new(this, assetPath);
        return animationPreviewVisibility.Step(click);
    }

    private sealed class AnimationPreviewVisibilityRun
    {
        private readonly EditorGame game;
        private readonly AnimationAssetPreview preview;
        private readonly ModelAssetPreview view;
        private readonly EditorDocumentEntry scene;
        private readonly EditorAuthoringState sceneState;
        private readonly DocumentGroupParityView initialScene;
        private readonly string assetPath;
        private readonly byte[] sceneBytes, assetBytes;
        private readonly Guid[] transactions;
        private readonly int historyPosition;
        private readonly PreviewFamilyLifetimeVerification lifetime;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private int phase, hiddenCount;
        private double hiddenSeconds, hiddenAge, hiddenAt;
        private long hiddenSceneRevision;
        private EngineOutputSample? hiddenOutput;
        private EngineOutputDifference? resumedDifference;
        private readonly List<object> receipts = [];

        internal AnimationPreviewVisibilityRun(EditorGame game, string assetPath)
        {
            this.game = game; this.assetPath = Path.GetFullPath(assetPath);
            preview = game.animationPreview ?? throw new InvalidOperationException("Open the actual compiled animation fixture first.");
            view = ReadFamilyField(preview, "view") as ModelAssetPreview
                ?? throw new InvalidOperationException("The animation's actual native model preview is unavailable.");
            scene = game.documents.Active ?? throw new InvalidOperationException("The original scene entry must remain active.");
            sceneState = scene.State ?? throw new InvalidOperationException("The original canonical scene is unavailable.");
            initialScene = game.InspectDocumentGroupParity(scene.Id)
                ?? throw new InvalidOperationException("The actual original scene pane is unavailable.");
            if (scene.Document is not EntityHierarchyDocument || !initialScene.Visible || !preview.Visible || preview.IsPlaying)
                throw new InvalidOperationException("Run after the actual animation Pause/output assertion with a visible original scene.");
            if (!string.Equals(game.assetPreviewPath, this.assetPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The animation preview must belong to the exact supplied native fixture asset.");
            sceneBytes = File.ReadAllBytes(scene.Path); assetBytes = File.ReadAllBytes(this.assetPath);
            historyPosition = game.sessionHistory!.History.Position;
            transactions = game.sessionHistory.Stack.RetrieveAllTransactions().Select(transaction => transaction.Id).ToArray();
            lifetime = game.CaptureFamilyLifetime("animation");
            Write("baseline");
        }

        private static int Identity(object? value) => value == null ? 0 : RuntimeHelpers.GetHashCode(value);
        private static string Sha(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
        internal object Inspection
        {
            get
            {
                var currentScene = game.InspectDocumentGroupParity(scene.Id);
                var actualTransactions = game.sessionHistory!.Stack.RetrieveAllTransactions().Select(transaction => transaction.Id).ToArray();
                var loadedRoots = lifetime.Cache?.GetLoadedAssetsSnapshot().Cast<object>().ToArray() ?? [];
                return new
                {
                    phase, elapsedSeconds = clock.Elapsed.TotalSeconds, assetPath, selectedAssetPath = game.selectedAssetPath,
                    ownerSame = ReferenceEquals(game.animationPreview, preview), preview.Visible, preview.IsPlaying,
                    preview.CurrentSeconds, preview.DurationSeconds, previewRenderCount = view.RenderCount,
                    previewDrawCalls = view.DrawCalls, previewRenderAgeSeconds = view.RenderAgeSeconds,
                    hiddenSeconds, hiddenCount, hiddenAge, hiddenAt,
                    nativeViewIdentity = Identity(view), nativeSceneSystemIdentity = Identity(ReadFamilyField(view, "sceneSystem")),
                    nativeContentIdentity = Identity(preview.SourceContent), nativeTargetOwnerIdentity = Identity(lifetime.Targets),
                    nativePreviewViewDisposed = view.IsDisposed,
                    targetDisposed = lifetime.Targets.IsDisposed,
                    registeredTargetPresent = game.renderer!.HasVerificationTexture(lifetime.Reference),
                    targetRetirementQueued = game.IsVerificationRetirementOwnerQueued(lifetime.Targets),
                    expectedNativeRootIdentities = lifetime.Roots.Select(Identity).ToArray(),
                    actualNativeRootIdentities = loadedRoots.Select(Identity).ToArray(),
                    nativeRootsRetained = lifetime.Roots.All(root => loadedRoots.Any(current => ReferenceEquals(current, root))),
                    selectedAssetSame = string.Equals(game.selectedAssetPath, assetPath, StringComparison.OrdinalIgnoreCase),
                    sceneStateSame = ReferenceEquals(scene.State, sceneState), sceneStatePublished = sceneState.IsPublished,
                    sceneStateDisposed = sceneState.IsDisposed,
                    sceneGeneration = sceneState.Generation, actualSceneGeneration = scene.State?.Generation,
                    sceneRuntimeSame = ReferenceEquals(currentScene?.Runtime, initialScene.Runtime),
                    sceneCameraSame = currentScene?.Camera == initialScene.Camera,
                    sceneVisible = currentScene?.Visible, sceneRenderRevision = currentScene?.RenderRevision,
                    hiddenSceneRevision, expectedHistoryPosition = historyPosition, actualHistoryPosition = game.sessionHistory.History.Position,
                    expectedTransactionIds = transactions, actualTransactionIds = actualTransactions,
                    historyPositionSame = historyPosition == game.sessionHistory.History.Position,
                    transactionIdsSame = actualTransactions.SequenceEqual(transactions),
                    expectedSceneSha256 = Sha(sceneBytes), actualSceneSha256 = Sha(File.ReadAllBytes(scene.Path)),
                    expectedAssetSha256 = Sha(assetBytes), actualAssetSha256 = Sha(File.ReadAllBytes(assetPath)),
                    sceneBytesSame = File.ReadAllBytes(scene.Path).AsSpan().SequenceEqual(sceneBytes),
                    assetBytesSame = File.ReadAllBytes(assetPath).AsSpan().SequenceEqual(assetBytes),
                    hiddenTimeSame = preview.CurrentSeconds == hiddenSeconds, hiddenRenderCountSame = view.RenderCount == hiddenCount,
                    hiddenWarmupSame = view.RenderAgeSeconds == hiddenAge,
                    gpuDifference = resumedDifference, receipts,
                    coverage = "Actual animation Play/Pause and View menu hide/restore, retained native model/cache/target, hidden render/time isolation and resumed GPU output while the original scene continues. Synthetic widget input; no OS input or all-family/backend claim. Existing family lifetime and outer terminal remain mandatory.",
                };
            }
        }
        private void Write(string reason)
        {
            var root = Environment.GetEnvironmentVariable("STRIDE_EDITOR_TEST_ARTIFACTS")
                ?? throw new InvalidOperationException("Native visibility verification requires its owned artifact directory.");
            File.WriteAllText(Path.Combine(root, "animation-preview-visibility-" + reason + ".json"), JsonSerializer.Serialize(Inspection));
        }
        private void Require(bool condition, string message)
        {
            if (condition) return;
            Write("failure"); throw new InvalidOperationException(message);
        }
        private void RequireOwners()
        {
            var currentScene = game.InspectDocumentGroupParity(scene.Id);
            var loadedRoots = lifetime.Cache?.GetLoadedAssetsSnapshot().Cast<object>().ToArray() ?? [];
            Require(ReferenceEquals(game.animationPreview, preview) && !view.IsDisposed && !lifetime.Targets.IsDisposed
                && game.renderer!.HasVerificationTexture(lifetime.Reference)
                && !game.IsVerificationRetirementOwnerQueued(lifetime.Targets)
                && lifetime.Roots.All(root => loadedRoots.Any(current => ReferenceEquals(current, root)))
                && ReferenceEquals(scene.State, sceneState) && sceneState.IsPublished && !sceneState.IsDisposed
                && ReferenceEquals(currentScene?.Runtime, initialScene.Runtime) && currentScene?.Camera == initialScene.Camera
                && string.Equals(game.selectedAssetPath, assetPath, StringComparison.OrdinalIgnoreCase)
                && game.sessionHistory!.History.Position == historyPosition
                && game.sessionHistory.Stack.RetrieveAllTransactions().Select(transaction => transaction.Id).SequenceEqual(transactions)
                && File.ReadAllBytes(scene.Path).AsSpan().SequenceEqual(sceneBytes)
                && File.ReadAllBytes(assetPath).AsSpan().SequenceEqual(assetBytes),
                "Animation visibility must retain exact native/canonical owners, authored history including redo, selected asset and authored files.");
        }
        internal bool Step(Func<string, bool> click)
        {
            Require(clock.Elapsed.TotalSeconds < 45, "Animation hide/restore exceeded its bounded native-input deadline.");
            RequireOwners();
            switch (phase)
            {
                case 0: if (click("animation-preview/play")) phase = 1; break;
                case 1:
                    if (!preview.IsPlaying) break;
                    if (click("menu/view")) phase = 2; break;
                case 2: if (click("menu/view/asset-preview")) phase = 3; break;
                case 3:
                    if (preview.Visible) break;
                    Require(preview.IsPlaying && view.DrawCalls == 0, "Hidden native animation must retain its playing intent without submitting draws.");
                    hiddenCount = view.RenderCount; hiddenSeconds = preview.CurrentSeconds; hiddenAge = view.RenderAgeSeconds;
                    hiddenSceneRevision = game.InspectDocumentGroupParity(scene.Id)!.RenderRevision;
                    hiddenOutput = game.ReadFamilyOutput("animation"); hiddenAt = clock.Elapsed.TotalSeconds; phase = 4; break;
                case 4:
                    Require(!preview.Visible && preview.IsPlaying && view.RenderCount == hiddenCount && view.DrawCalls == 0
                        && preview.CurrentSeconds == hiddenSeconds && view.RenderAgeSeconds == hiddenAge,
                        "Hidden animation must not advance native clip time, model warmup or GPU submissions across the sampled interval.");
                    if (clock.Elapsed.TotalSeconds - hiddenAt < .25) break;
                    var sceneView = game.InspectDocumentGroupParity(scene.Id);
                    if (sceneView is not { Visible: true } || sceneView.RenderRevision <= hiddenSceneRevision) break;
                    var unchanged = hiddenOutput!.Compare(game.ReadFamilyOutput("animation"), 0, 0, hiddenOutput.Width, hiddenOutput.Height);
                    Require(unchanged.ChangedPixels == 0, "Hidden animation target must retain its exact native GPU output.");
                    receipts.Add(new { contract = "hidden-native-animation-isolation", durationSeconds = clock.Elapsed.TotalSeconds - hiddenAt,
                        hiddenCount, hiddenSeconds, hiddenAge, sceneRenderAdvanced = true, unchanged.ChangedPixels });
                    if (click("menu/view")) phase = 5; break;
                case 5: if (click("menu/view/asset-preview")) phase = 6; break;
                case 6:
                    if (!preview.Visible || view.RenderCount <= hiddenCount || preview.CurrentSeconds == hiddenSeconds) break;
                    resumedDifference = hiddenOutput!.Compare(game.ReadFamilyOutput("animation"), 0, 0, hiddenOutput.Width, hiddenOutput.Height);
                    if (resumedDifference.Value.ChangedPixels < 16) break;
                    if (click("animation-preview/pause")) phase = 7; break;
                case 7:
                    if (preview.IsPlaying) break;
                    Require(view.Visible && view.RenderCount > hiddenCount && view.DrawCalls > 0,
                        "The same native animation preview must resume rendering and accept Pause after panel restoration.");
                    receipts.Add(new { contract = "same-owner-resume-and-native-pause", view.RenderCount, preview.CurrentSeconds,
                        difference = resumedDifference });
                    phase = 8; Write("passed"); return true;
                case 8: return true;
            }
            return false;
        }
    }
}
