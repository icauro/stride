using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

/// <summary>Five real preview workflows share one canonical loaded project; each preview retires before the next case.</summary>
internal sealed class EnginePreviewGroupCapabilityProbe : EngineUiCapabilityProbe
{
    private static readonly string[] Identities = ["E03.texture", "E03.model", "E03.animation", "E03.sprite", "E03.font"];
    private readonly Dictionary<string, PreviewFamilyFixtureVerification> fixtures = [];
    private readonly List<EngineUiCapabilityProbe> completed = [];
    private readonly List<object> retirements = [];
    private EngineUiCapabilityProbe? current;
    private PreviewLifetimeVerificationOwner? retiring;
    private SceneDocument? documentOwner;
    private object? workspaceOwner, historyOwner, compiledOwner;
    private Guid[] history = [];
    private int index, pendingOwners;
    protected override string Scenario => "E03.group";
    protected override double PhaseTimeoutSeconds => Phase <= 3 ? 180 : 45;
    protected override object Evidence => new
    {
        cases = Identities, currentCase = index < Identities.Length ? Identities[index] : null,
        completedCases = completed.Count,
        sameWorkspace = workspaceOwner != null && ReferenceEquals(workspaceOwner, Game.VerificationWorkspaceOwner),
        sameCompiledContent = compiledOwner != null && ReferenceEquals(compiledOwner, Game.VerificationCompiledProjectOwner),
        retirements, currentRetirement = retiring?.Snapshot(Game),
        pendingOwners, currentPendingOwners = Game.VerificationPendingPreviewOwners,
        terminalTeardown = "Individual results remain provisional until the outer group's actual terminal retirement completes."
    };
    internal EnginePreviewGroupCapabilityProbe(EditorGame game, string artifacts) : base(game, artifacts)
    { SharedTerminalTeardownPath = Path.Combine(Artifacts, "teardown-result.json"); }
    protected override void Step()
    {
        var io = Gui.GetIO();
        switch (Phase)
        {
            case 0:
                if (fixtures.Count == 0)
                    foreach (var family in new[] { "animation", "sprite", "font" })
                        fixtures.Add(family, Game.PreparePreviewFamilyFixture(family));
                if (Click("toolbar/open")) Next(); break;
            case 1: if (Click("project/open")) Next(); break;
            case 2:
                if (Game.Document == null || !EnsureScenePresented()) break;
                Game.AssertCompiledSceneVerification();
                documentOwner = Game.Document;
                Require(!documentOwner.IsDirty && !documentOwner.CanUndo, "Preview group must begin with a real clean scene.");
                workspaceOwner = Game.VerificationWorkspaceOwner; historyOwner = Game.VerificationHistoryOwner;
                compiledOwner = Game.VerificationCompiledProjectOwner; history = Game.VerificationHistoryTransactions;
                pendingOwners = Game.VerificationPendingPreviewOwners;
                Next(); break;
            case 3:
                RequireSharedProject();
                current ??= CreateCase();
                current.Update();
                if (!current.Finished) break;
                completed.Add(current);
                Require(current.Passed, Identities[index] + " failed: " + current.Result);
                if (index < 2) retiring = Game.CapturePreviewLifetimeVerification(index == 1);
                Next(); break;
            case 4: if (Click("asset/filter")) Next(); break;
            case 5: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 6:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var c in "GameSettings") io.AddInputCharacter(c);
                Next(); break;
            case 7: io.AddKeyEvent(ImGuiKey.ModShift, true); io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 8: io.AddKeyEvent(ImGuiKey.Enter, false); io.AddKeyEvent(ImGuiKey.ModShift, false); Next(); break;
            case 9: if (Click("asset/GameSettings")) Next(); break;
            case 10:
                RequireSharedProject();
                if (Game.ModelPreview != null || Game.TexturePreview != null
                    || Game.VerificationAnimation != null || Game.VerificationSprite != null || Game.VerificationFont != null) break;
                if (retiring != null && !retiring.IsSettled(Game)) break;
                if (Game.VerificationPendingPreviewOwners > pendingOwners) break;
                Require(Game.SelectedAssetPath?.EndsWith(".sdgamesettings", StringComparison.OrdinalIgnoreCase) == true,
                    "Real browser settings selection must close each preview.");
                retirements.Add(new { scenario = Identities[index], nativeOwner = retiring?.Snapshot(Game),
                    pending = Game.VerificationPendingPreviewOwners, familyOwnRetirementVerified = index >= 2 });
                retiring = null; current = null; index++;
                if (index == Identities.Length)
                    Finish(true, "E03.group PASS: five independent native preview workflows share one actual compiled project; each preview retires before the next and all require the final terminal drain.");
                else SeekPhase(3);
                break;
        }
    }
    private EngineUiCapabilityProbe CreateCase()
    {
        var artifacts = Path.Combine(Artifacts, Identities[index]);
        EngineUiCapabilityProbe probe = index switch
        {
            0 => new EnginePreviewCapabilityProbe(Game, artifacts, false, alreadyLoaded: true),
            1 => new EnginePreviewCapabilityProbe(Game, artifacts, true, alreadyLoaded: true),
            _ => new EnginePreviewFamilyProbe(Game, artifacts, Identities[index][4..], fixtures[Identities[index][4..]])
        };
        probe.SharedTerminalTeardownPath = SharedTerminalTeardownPath;
        return probe;
    }
    private void RequireSharedProject()
    {
        Require(ReferenceEquals(Game.Document, documentOwner) && documentOwner is { IsDirty: false, CanUndo: false }
            && ReferenceEquals(workspaceOwner, Game.VerificationWorkspaceOwner)
            && ReferenceEquals(historyOwner, Game.VerificationHistoryOwner)
            && ReferenceEquals(compiledOwner, Game.VerificationCompiledProjectOwner)
            && history.SequenceEqual(Game.VerificationHistoryTransactions),
            "Grouped previews must retain the exact loaded document/workspace/compiled-content/history owners.");
    }
    protected override void CleanInput() => Gui.GetIO().AddKeyEvent(ImGuiKey.ModShift, false);
    internal override void FinalizeCompletion(bool passed, string result)
    {
        foreach (var probe in completed)
            probe.FinalizeCompletion(passed && probe.Passed,
                passed ? probe.Result : probe.Result + Environment.NewLine + "Group completion: " + result);
        base.FinalizeCompletion(passed, result);
    }
}
