namespace Stride.Editor.ImGui;

/// <summary>Real pending-picker/document-generation integration boundary; reload is a service action, not claimed UI input.</summary>
internal sealed class EngineStalePickGenerationProbe : EngineUiCapabilityProbe
{
    private StalePickGenerationVerification? ownership;
    private long observedRender = -1;
    protected override string Scenario => "E02.staleGeneration";
    protected override object Evidence => new
    {
        previous = ownership?.PreviousGeneration,
        next = ownership?.NextGeneration,
        cancelled = ownership?.PreviousPicker.CancelledRequests,
        readbacks = ownership?.PreviousPicker.ReadbackCount,
        input = "UI project opening and selection; actual native queue plus product reload service at generation boundary",
        gaps = new[] { "This deterministically exercises generation replacement; physical click/switch timing remains separate." },
    };
    internal EngineStalePickGenerationProbe(EditorGame game, string artifacts) : base(game, artifacts) { }
    protected override void Step()
    {
        switch (Phase)
        {
            case 0: if (Click("toolbar/open")) Next(); break;
            case 1: if (Click("project/open")) Next(); break;
            case 2:
                if (Game.Document != null && EnsureScenePresented() && Click("entity/Camera")) Next();
                break;
            case 3:
                if (Game.Document?.Selected?.Name != "Camera") break;
                ownership = Game.QueuePickAndReloadGenerationForVerification();
                Require(!ReferenceEquals(ownership.PreviousOwner, ownership.NextOwner)
                    && ownership.PreviousOwner.Asset.Id == ownership.NextOwner.Asset.Id
                    && ownership.PreviousGeneration != ownership.NextGeneration,
                    "Reload must publish a genuinely distinct canonical generation of the same authored asset");
                Next(); break;
            case 4:
                if (!ReferenceEquals(Game.Document, ownership!.NextOwner) || !EnsureScenePresented()) break;
                if (observedRender < 0) { observedRender = Game.VerificationSceneRenderRevision; break; }
                if (Game.VerificationSceneRenderRevision <= observedRender) break;
                Require(!ownership.PreviousPicker.IsPending && !ownership.PreviousPicker.HasResult
                    && ownership.PreviousPicker.ReadbackCount == ownership.Readbacks
                    && ownership.PreviousPicker.CancelledRequests > ownership.Cancellations,
                    "Old-generation native request must cancel without GPU readback or a consumable stale result");
                Require(ownership.NextOwner.Selected?.Id == ownership.NextSelection && !ownership.NextOwner.IsDirty,
                    "Old pick must not mutate replacement generation selection or authored data");
                Game.AssertCompiledSceneVerification();
                Game.SaveSceneVerificationOutput(Path.Combine(Artifacts, "replacement-generation.png"));
                Finish(true, "E02 generation boundary PASS: genuine pending native pick canceled on canonical scene reload, new generation keeps its own selection/data and renders compiled output.");
                break;
        }
    }
}
