using Stride.Assets.Entities;
using Stride.Core.IO;
using Stride.Games;
using Stride.Graphics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private object? compositorParityEvidence;
    private bool compositorParityPrepared;
    private bool compositorParityRetirementVerified;
    private sealed record CompositorParityRetirement(EngineViewport Viewport, Stride.Engine.Game Game, Texture Target);
    private readonly List<CompositorParityRetirement> compositorParityRetirements = [];
    internal object? CompositorParityVerificationEvidence => compositorParityEvidence;
    internal bool StepTwoViewSceneCompositorReplacementParity()
    {
        if (compositorParityRetirementVerified) return true;
        if (!compositorParityPrepared)
        {
            VerifyTwoViewSceneCompositorReplacementParity();
            compositorParityPrepared = true;
        }
        if (compositorParityRetirements.Count < 2)
            throw new InvalidOperationException("Compositor retirement requires both exact replaced native scene authorities.");
        if (compositorParityRetirements.Any(item => !item.Viewport.IsSceneRetirementDrainComplete
            || !item.Viewport.IsResourceRetirementComplete)) return false;
        foreach (var item in compositorParityRetirements)
            if (!item.Game.IsDisposed || !item.Target.IsDisposed || item.Viewport.RuntimeGame != null
                || item.Viewport.RuntimeScene != null || item.Viewport.Target != null)
                throw new InvalidOperationException("Certified compositor replacement retained an exact old native game, scene or GPU target.");
        compositorParityEvidence = new
        {
            replacement = compositorParityEvidence, exactOldAuthorityRetirements = compositorParityRetirements.Count,
            exactOldGamesDisposed = true, exactOldTargetsDisposed = true,
            nativeCpuAndGpuRetirementCompleted = true
        };
        compositorParityRetirementVerified = true;
        return true;
    }
    // Reuses the native E05 fixture preparation before the single project load:
    // actual AssetQuantum hierarchy cloning, identity/reference verification,
    // serialization and a package root asset make the second scene compilable.
    internal MultiViewFixtureVerification PrepareTwoViewCompositorParityFixture()
    {
        if (!Environment.GetCommandLineArgs().Contains("--ui-test"))
            throw new InvalidOperationException("Two-view preparation requires a private UI-test launch.");
        return PrepareMultiViewVerificationFixture();
    }
    internal void VerifyTwoViewSceneCompositorReplacementParity()
    {
        if (!Environment.GetCommandLineArgs().Contains("--ui-test") || workspace == null)
            throw new InvalidOperationException("Two-view replacement requires the loaded private UI fixture.");
        var active = documents.Active;
        if (active?.Document is not EntityHierarchyDocument || engineViewport?.Target is not { } framebuffer)
            throw new InvalidOperationException("The initial canonical scene must have a real allocated target.");
        var secondAsset = workspace.AssetItems.SingleOrDefault(item => item.Asset is SceneAsset
            && Path.GetFileName(item.FullPath.ToOSPath()) == "CapabilitySecondScene.sdscene")
            ?? throw new InvalidOperationException("Prepare the existing native two-scene fixture before project loading.");
        var path = secondAsset.FullPath.ToOSPath();
        var existing = documents.Find(path);
        EditorDocumentEntry? second = null;
        try
        {
            OpenRetainedDocument(path, scene: true, reload: false);
            second = documents.Find(path) ?? throw new InvalidOperationException("The second scene did not open through the canonical document route.");
            var view = EnsureSceneDocumentView(second);
            view.Viewport.Resize(framebuffer.Width, framebuffer.Height);
            if (ReferenceEquals(second, active) || ReferenceEquals(view.Viewport, engineViewport)
                && ReferenceEquals(second.Document, active.Document))
                throw new InvalidOperationException("Two-view verification requires distinct actual scene documents.");
            ActivateDocument(active);
            PrepareSceneCompositorReload(out var scope);
            if (scope == null || scope.Targets.Select(target => target.Presented.Asset.Id).Distinct().Count() < 2
                || scope.Targets.Select(target => target.Viewport.RuntimeGame).Distinct().Count() < 2)
                throw new InvalidOperationException("Both retained canonical scenes must expose independent real native game authorities.");
            VerifyTwoViewSceneGenerationHistoryParity(active, second);
            VerifySceneCompositorReplacementParity();
        }
        finally
        {
            if (documents.Entries.Contains(active) && !ReferenceEquals(documents.Active, active)) ActivateDocument(active);
            if (existing == null && second != null && documents.Entries.Contains(second))
                RequestDocumentClose(second);
        }
    }
    // Execute on the ownership actor after a private fixture has prepared its
    // retained scene views. Uses the actual loaded compositor bundle, so it adds
    // no compiler job and does not claim authored-compositor compilation coverage.
    internal void VerifySceneCompositorReplacementParity()
    {
        if (!Environment.GetCommandLineArgs().Contains("--ui-test"))
            throw new InvalidOperationException("Compositor verification requires a private UI-test launch.");
        PrepareSceneCompositorReload(out var original);
        if (original == null || original.Targets.Length == 0)
            throw new InvalidOperationException("No canonical native scene compositor targets are ready.");
        void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        var target = original.Targets[0]; var viewport = target.Viewport;
        var previousAttempt = sceneCompositorReloadAttempt;
        var width = viewport.Target?.Width ?? 0; var height = viewport.Target?.Height ?? 0;
        var available = viewport.RuntimeSourceAvailable;
        Require(width > 0 && height > 0, "The native scene target must be allocated before verification.");
        try
        {
            RememberSceneCompositorReloadAttempt(original, "native-certificate-check");
            PrepareSceneCompositorReload(out var unchanged);
            Require(unchanged != null && IsSameSceneCompositorReloadAttempt(unchanged, "native-certificate-check"),
                "An unchanged actual scene must retain the bounded failure-attempt certificate.");
            viewport.SetRuntimeSourceFailed(available);
            Require(!IsCurrentSceneCompositorTarget(target), "A native source-availability transition must invalidate its target certificate.");
            PrepareSceneCompositorReload(out var changedRuntime);
            Require(changedRuntime != null && !IsSameSceneCompositorReloadAttempt(changedRuntime, "native-certificate-check"),
                "Native runtime changes must permit retry without an authored document edit.");
            viewport.SetRuntimeSourceFailed(!available);
            PrepareSceneCompositorReload(out var beforeResize);
            Require(beforeResize != null, "Restored native availability must have a current scope.");
            var sizeTarget = beforeResize!.Targets.Single(item => ReferenceEquals(item.Viewport, viewport));
            RememberSceneCompositorReloadAttempt(beforeResize, "native-size-check");
            viewport.Resize(width == 4096 ? width - 1 : width + 1, height);
            Require(!IsCurrentSceneCompositorTarget(sizeTarget), "An actual target extent change must reject the old preparation certificate.");
            PrepareSceneCompositorReload(out var changedSize);
            Require(changedSize != null && !IsSameSceneCompositorReloadAttempt(changedSize, "native-size-check"),
                "An actual extent change must permit compositor retry without an authored edit.");
        }
        finally
        {
            viewport.SetRuntimeSourceFailed(!available);
            viewport.Resize(width, height);
            sceneCompositorReloadAttempt = previousAttempt;
        }
        PrepareSceneCompositorReload(out var replacementScope);
        Require(replacementScope != null, "Canonical replacement scope must remain available after native certificate checks.");
        var states = replacementScope!.Targets.Select(item => new
        {
            Target = item, Game = item.Viewport.RuntimeGame, Scene = item.Viewport.RuntimeScene,
            Camera = item.Viewport.Camera.Capture(item.Viewport.ShowSelection),
            Selection = item.Viewport.ShowSelection, MaterialSelection = item.Viewport.MaterialSelectionEnabled,
            Visible = item.Viewport.Visible, Width = item.Viewport.Target?.Width, Height = item.Viewport.Target?.Height,
            Framebuffer = item.Viewport.Target
        }).ToArray();
        var activeDocument = document; var activeEntry = documents.Active;
        foreach (var item in states)
        {
            // Real actor publication, native cache/scene recreation, normal
            // canonical view adoption and certified old-authority retirement.
            RecreateSceneCompositorViewport(replacementScope, item.Target, item.Target.Content);
            var next = item.Target.View != null ? sceneDocumentViews[item.Target.View.Entry.Id].Viewport : engineViewport;
            Require(next != null && !ReferenceEquals(next, item.Target.Viewport)
                && !ReferenceEquals(next.RuntimeGame, item.Game) && !ReferenceEquals(next.RuntimeScene, item.Scene),
                "Each canonical scene must install a fresh actual native authority.");
            Require(ReferenceEquals(next!.PresentedDocument, item.Target.Presented)
                && next.LoadedCompositorId == replacementScope.Compositor
                && next.Camera.Capture(next.ShowSelection) == item.Camera
                && next.ShowSelection == item.Selection && next.MaterialSelectionEnabled == item.MaterialSelection
                && next.Visible == item.Visible && next.Target?.Width == item.Width && next.Target?.Height == item.Height,
                "Compositor replacement must preserve actual owner, camera, selection policy, visibility and target extent.");
            compositorParityRetirements.Add(new(item.Target.Viewport,
                item.Game ?? throw new InvalidOperationException("The replaced compositor has no actual native game."),
                item.Framebuffer ?? throw new InvalidOperationException("The replaced compositor has no actual GPU framebuffer.")));
        }
        Require(ReferenceEquals(document, activeDocument) && ReferenceEquals(documents.Active, activeEntry),
            "Replacing inactive retained views must not swap the active authored owner.");
        compositorParityEvidence = new { targetCount = states.Length, nativeRuntimeCertificateInvalidated = true,
            generationHistory = sceneGenerationParityEvidence,
            actualExtentCertificateInvalidated = true, realNativeAuthoritiesRecreated = true,
            authoredCompilationCovered = false, physicalFloatingCovered = false };
    }
}
