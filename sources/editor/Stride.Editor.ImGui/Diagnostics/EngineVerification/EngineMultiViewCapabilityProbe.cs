using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed class EngineMultiViewCapabilityProbe : EngineUiCapabilityProbe
{
    private MultiViewFixtureVerification? fixture;
    private SceneDocument? first;
    private EntityHierarchyDocument? second;
    private EngineViewport? firstView, secondView;
    private Guid firstTab, secondTab;
    private Matrix firstCamera, secondCamera, changedSecondCamera;
    private Guid? firstSelection;
    private Guid? secondSelection;
    private bool wheelSent;
    private bool secondTabFocused;
    private bool closedSecond;
    private bool assetSelectionClearedFirstScope, returnedFirstSelectionVerified;
    private long closeRenderRevision;
    private EngineOutputSample? firstOutput, secondOutput;
    protected override string Scenario => "E05";
    protected override object Evidence => new { fixture = fixture == null ? null : new
        { sceneId = fixture.SceneId.ToString(), fixture.ScenePath, fixture.PackagePath }, firstTab, secondTab,
        firstScene = firstView?.CompiledSceneUrl, secondScene = secondView?.CompiledSceneUrl,
        independentRuntime = firstView != null && secondView != null && !ReferenceEquals(firstView.RuntimeScene, secondView.RuntimeScene),
        independentCompositors = firstView != null && secondView != null && !ReferenceEquals(firstView.VerificationCompositorOwner, secondView.VerificationCompositorOwner),
        closedSecond, assetSelectionClearedFirstScope, returnedFirstSelectionVerified, wheelSent, secondTabFocused,
        selectionContract = "Native WPF SelectionService clears other registered scopes when a new selection is added; retained view cameras and native world owners remain independent.",
        firstExpectedSelection = firstSelection, firstActualSelection = first?.Selected?.Id,
        secondExpectedSelection = secondSelection, secondActualSelection = second?.Selected?.Id,
        firstExpectedCamera = firstCamera.ToArray(), firstActualCamera = firstView?.Camera.Component.ViewMatrix.ToArray(),
        secondExpectedCamera = changedSecondCamera.ToArray(), secondActualCamera = secondView?.Camera.Component.ViewMatrix.ToArray(),
        gaps = new[] { "Simultaneously visible split groups and device recovery remain separate ownership cases." } };
    internal EngineMultiViewCapabilityProbe(EditorGame game, string artifacts) : base(game, artifacts) { }
    protected override void Step()
    {
        var io = Gui.GetIO();
        switch (Phase)
        {
            case 0:
                fixture ??= Game.PrepareMultiViewVerificationFixture();
                if (Click("toolbar/open")) Next(); break;
            case 1: if (Click("project/open")) Next(); break;
            case 2:
                if (firstOutput == null)
                {
                if (Game.Document == null || !EnsureScenePresented()) break;
                if (Game.Document.Selected?.Name != "Sphere") { Click("entity/Sphere"); break; }
                first = Game.Document; firstView = Game.VerificationDocumentViewport(first);
                Require(firstView?.RuntimeScene != null && firstView.VerificationCompositorOwner != null, "First document must own its real compiled runtime/compositor.");
                firstTab = Game.VerificationDocumentId(first)!.Value;
                firstCamera = firstView!.Camera.Component.ViewMatrix; firstSelection = first.Selected?.Id;
                firstOutput = Game.ReadSceneVerificationOutput();
                Game.SaveSceneVerificationOutput(Path.Combine(Artifacts, "E05-first.png"));
                }
                if (Click("asset/filter")) Next(); break;
            case 3: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 4:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var c in "CapabilitySecondScene") io.AddInputCharacter(c); Next(); break;
            case 5: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 6: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 7: if (Click("asset/CapabilitySecondScene")) Next(); break;
            case 8:
                if (!assetSelectionClearedFirstScope)
                {
                    Require(first!.SelectedCount == 0 && Game.VerificationAssetBrowserPrimary == fixture!.SceneId,
                        "Selecting the second scene asset must clear the first hierarchy scope as native WPF selection does.");
                    assetSelectionClearedFirstScope = true;
                }
                if (Click("asset/CapabilitySecondScene")) Next(); break;
            case 9:
                second = Game.VerificationActiveHierarchy;
                if (second == null || second.Asset.Id != fixture!.SceneId) break;
                secondView = Game.VerificationDocumentViewport(second);
                if (secondView?.RuntimeScene == null || !secondView.Visible || secondView.Target == null) break;
                if (second.Selected?.Name != "Secondary Imported cube") { Click("entity/Secondary Imported cube"); break; }
                secondSelection = second.Selected.Id;
                secondTab = Game.VerificationDocumentId(second)!.Value;
                Require(!ReferenceEquals(firstView, secondView) && !ReferenceEquals(firstView!.RuntimeScene, secondView.RuntimeScene)
                    && firstView.Target != null && !ReferenceEquals(firstView.Target, secondView.Target)
                    && firstView.VerificationCompositorOwner != null && secondView.VerificationCompositorOwner != null
                    && !ReferenceEquals(firstView.VerificationCompositorOwner, secondView.VerificationCompositorOwner),
                    "Retained documents must own independent real view/runtime/compositor instances.");
                Require(firstView.Camera.Component.ViewMatrix == firstCamera && first!.SelectedCount == 0,
                    "Opening a second document must preserve the first native camera and the globally cleared first selection scope.");
                Require(!first.Asset.Hierarchy.Parts.ContainsKey(secondSelection!.Value),
                    "Second-world selection must use a distinct authored identity outside the first world's parts.");
                Game.AssertRuntimeEntityVerification(secondSelection.Value, second.Selected.Transform.Parent?.Entity.Id);
                secondCamera = secondView.Camera.Component.ViewMatrix;
                secondOutput = Game.ReadSceneVerificationOutput();
                Require(firstView.CompiledSceneUrl != secondView.CompiledSceneUrl, "Both runtimes must use their distinct compiled scene assets.");
                Game.SaveSceneVerificationOutput(Path.Combine(Artifacts, "E05-second.png")); Next(); break;
            case 10:
                if (!secondTabFocused)
                {
                    if (Click("document-tab/" + secondTab)) secondTabFocused = true;
                    break;
                }
                if (!wheelSent)
                {
                    if (!Game.Controls.Centers.TryGetValue("scene/image", out var point)) break;
                    if (!Aim("E05-second-scene-wheel", point)) break;
                    io.AddMouseWheelEvent(0, 1); wheelSent = true; break;
                }
                if (secondView!.Camera.Component.ViewMatrix == secondCamera) break;
                changedSecondCamera = secondView.Camera.Component.ViewMatrix;
                Require(firstView!.Camera.Component.ViewMatrix == firstCamera && !first!.IsDirty && !second!.IsDirty,
                    "Actual second-view camera input must not modify first camera or authored data.");
                if (Click("document-tab/" + firstTab)) Next(); break;
            case 11:
                if (!ReferenceEquals(Game.VerificationActiveHierarchy, first) || firstView!.Visible != true) break;
                if (first!.Selected?.Id != firstSelection) { Click("entity/Sphere"); break; }
                Require(ReferenceEquals(Game.VerificationDocumentViewport(first!), firstView)
                    && ReferenceEquals(Game.VerificationDocumentViewport(second!), secondView)
                    && firstView.Camera.Component.ViewMatrix == firstCamera && first!.Selected?.Id == firstSelection
                    && secondView!.Camera.Component.ViewMatrix == changedSecondCamera && !secondView.Visible,
                    "Switching/hiding must retain exact view owners and independent camera/selection state.");
                Require(second!.SelectedCount == 0, "Selecting the first world must clear the second scope as native WPF selection does.");
                Game.AssertRuntimeEntityVerification(firstSelection!.Value, first.Selected.Transform.Parent?.Entity.Id);
                returnedFirstSelectionVerified = true;
                Game.ReadSceneVerificationOutput();
                if (Click("document-tab/" + secondTab)) Next(); break;
            case 12:
                if (!ReferenceEquals(Game.VerificationActiveHierarchy, second) || secondView!.Visible != true) break;
                Require(ReferenceEquals(Game.VerificationDocumentViewport(second!), secondView)
                    && secondView.Camera.Component.ViewMatrix == changedSecondCamera
                    && second!.SelectedCount == 0 && first!.Selected?.Id == firstSelection
                    && firstView!.Camera.Component.ViewMatrix == firstCamera && !firstView.Visible,
                    "Resuming the second view must preserve its actual camera and WPF global selection state while leaving first hidden.");
                Game.ReadSceneVerificationOutput();
                if (!Game.Controls.Centers.TryGetValue("document-tab/" + secondTab, out var closePoint)
                    || !Aim("E05-close-second", closePoint)) break;
                // Native ImGui tab closure uses its documented middle-button gesture.
                io.AddMouseButtonEvent(2, true); closeRenderRevision = firstView.VerificationRenderRevision; Next();
                break;
            case 13: io.AddMouseButtonEvent(2, false); Next(); break;
            case 14:
                if (Game.VerificationDocumentId(second!) != null) break;
                if (!ReferenceEquals(Game.VerificationActiveHierarchy, first))
                { Click("document-tab/" + firstTab); break; }
                if (!firstView!.Visible || firstView.VerificationRenderRevision <= closeRenderRevision) break;
                Require(ReferenceEquals(Game.VerificationDocumentViewport(first!), firstView)
                    && firstView.Camera.Component.ViewMatrix == firstCamera && first!.Selected?.Id == firstSelection,
                    "Closing the second document must preserve the remaining real view, camera and selection.");
                if (!secondView!.IsSceneRetirementDrainComplete || !secondView.IsResourceRetirementComplete
                    || secondView.Target != null || secondView.RuntimeScene != null) break;
                EngineCapabilityAssertions.RetiredScene(secondView);
                Game.ReadSceneVerificationOutput();
                Game.SaveSceneVerificationOutput(Path.Combine(Artifacts, "E05-remaining-after-close.png"));
                closedSecond = true;
                Finish(true, "E05 PASS: independent compiled view/runtime/compositor owners and cameras, WPF globally exclusive selection with real first-world reselection, hide/resume and native tab close retirement.");
                break;
        }
    }
    protected override void CleanInput() => Gui.GetIO().AddMouseButtonEvent(2, false);
}
