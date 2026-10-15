using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

/// <summary>E06 preview lifecycle subset; retirement is proved by actual native owners and registrations.</summary>
internal sealed class EngineResourceLifecycleProbe : EngineUiCapabilityProbe
{
    private readonly List<PreviewLifetimeVerificationOwner> owners = [];
    private readonly List<object> settledEvidence = [];
    private SceneDocument? scene;
    private Matrix camera;
    private int round, initialPendingOwners, lastRecordedRound = -1;
    protected override string Scenario => "E06";
    private bool Model => round % 2 == 1;
    private string AssetName => round == 4 ? "GameSettings" : Model ? "Imported cube" : "Skybox texture";
    protected override object Evidence => new { round, initialPendingOwners, pendingOwners = Game.VerificationPendingPreviewOwners,
        owners = owners.Select(owner => owner.Snapshot(Game)).ToArray(), settledEvidence,
        gaps = new[] { "Project replacement, document lifecycle, device recovery and other preview families require separate coverage." } };
    internal EngineResourceLifecycleProbe(EditorGame game, string artifacts) : base(game, artifacts) { }
    protected override void Step()
    {
        var io = Gui.GetIO();
        switch (Phase)
        {
            case 0: if (Click("toolbar/open")) Next(); break;
            case 1: if (Click("project/open")) Next(); break;
            case 2:
                if (scene == null)
                {
                    if (Game.Document == null || !EnsureScenePresented()) break;
                    scene = Game.Document; camera = Game.VerificationSceneCamera;
                    initialPendingOwners = Game.VerificationPendingPreviewOwners;
                }
                if (Click("asset/filter")) Next(); break;
            case 3: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 4:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var c in AssetName) io.AddInputCharacter(c); Next(); break;
            case 5: io.AddKeyEvent(ImGuiKey.ModShift, true); io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 6: io.AddKeyEvent(ImGuiKey.Enter, false); io.AddKeyEvent(ImGuiKey.ModShift, false); Next(); break;
            case 7: if (Click("asset/" + AssetName)) Next(); break;
            case 8:
                Require(ReferenceEquals(Game.Document, scene) && !scene!.IsDirty && !scene.CanUndo && Game.VerificationSceneCamera == camera,
                    "Preview replacement must retain clean authored scene and independent scene camera.");
                if (round == 4)
                {
                    if (Game.TexturePreview != null || Game.ModelPreview != null) break;
                    Require(Game.SelectedAssetPath?.EndsWith(".sdgamesettings", StringComparison.OrdinalIgnoreCase) == true,
                        "The final real settings selection must close the compiled texture/model preview.");
                    Next(); break;
                }
                if (Model)
                {
                    if (Game.ModelPreview is not { RenderCount: > 0 } model) break;
                    Require(model.RenderError == null && model.SourceContent != null && model.MeshCount > 0 && model.VertexBytes > 0,
                        "The repeated model preview must load actual compiled GPU resources.");
                }
                else
                {
                    if (Game.TexturePreview is not { RenderCount: > 0 } texture) break;
                    Require(texture.RenderError == null && texture.Width > 0 && texture.Height > 0,
                        "The repeated texture preview must load actual compiled content.");
                }
                var next = Game.CapturePreviewLifetimeVerification(Model);
                Require(owners.All(previous => !ReferenceEquals(previous.Preview, next.Preview)),
                    "Every reopen must create a distinct actual preview owner.");
                owners.Add(next);
                // A complete actual displayed-target readback accompanies each owner;
                // E03 independently tests the asset-specific visible control/output result.
                Game.ReadPreviewVerificationOutput(Model);
                Game.SavePreviewVerificationOutput(Model, Path.Combine(Artifacts, $"E06-round-{round}.png"));
                Next(); break;
            case 9:
                var retired = round == 4 ? owners : owners.Take(owners.Count - 1);
                if (retired.Any(owner => !owner.IsSettled(Game))) break;
                if (Game.VerificationPendingPreviewOwners > initialPendingOwners) break;
                if (lastRecordedRound != round)
                {
                    settledEvidence.Add(new { round, pendingOwners = Game.VerificationPendingPreviewOwners,
                        retiredOwners = retired.Select(owner => owner.Snapshot(Game)).ToArray() });
                    lastRecordedRound = round;
                }
                if (round == 4)
                {
                    Require(owners.Count == 4 && owners.All(owner => owner.IsSettled(Game)),
                        "All four real preview/cache/target owners must finish retirement.");
                    Finish(true, "E06 preview subset PASS: repeated compiled texture/model replacement and final UI close settle actual cache roots, GPU target owners and renderer registrations.");
                }
                else if (Click("asset/filter")) { round++; Phase = 2; Next(); }
                break;
        }
    }
    protected override void CleanInput() => Gui.GetIO().AddKeyEvent(ImGuiKey.ModShift, false);
}
