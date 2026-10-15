namespace Stride.Editor.ImGui;

internal sealed class EngineAssetReimportCapabilityProbe(EditorGame game, string artifacts) : EngineUiCapabilityProbe(game, artifacts)
{
    private AssetReimportParityRun? run;
    internal object CapabilityEvidence => Evidence;
    protected override string Scenario => "asset-templates.reimport-changed-batch";
    protected override double PhaseTimeoutSeconds => 120;
    protected override object Evidence => Game.CaptureAssetReimportParityState(run);
    protected override void Step()
    {
        if (run == null)
        {
            if (!Game.AssetReimportParityReady) return;
            run = Game.BeginAssetReimportParityVerification(Artifacts);
        }
        var before = run.Stage;
        if (Game.StepAssetReimportParityVerification(run))
            Finish(true, "Actual changed-source texture batch reimport, only-modified exclusion, native cancellation-to-terminal, IDs/hash metadata/history/Save and original baseline restoration passed.");
        else if (run.Stage != before) SeekPhase(3 + run.Stage);
    }
    protected override void CleanInput()
    {
        if (run != null) Game.RetireAssetReimportParityVerification(run);
    }
}
