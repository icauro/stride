namespace Stride.Editor.ImGui;

internal sealed partial class UiWorkflowTest
{
    private const string CompileFailureMarker = "U01 intentional fixture compilation failure";
    private string? failedLoadSource;
    private byte[]? failedLoadSourceBackup;
    private SceneDocument? failurePreservedScene;
    private Guid[] failurePreservedEntityIds = [];

    private void UpdateLoadFailureRecoveryScenario()
    {
        switch (stage)
        {
            case 10400:
                failurePreservedScene = game.Document;
                Assert(failurePreservedScene is { IsDirty: false }, "Failure recovery requires a real clean prior scene");
                failurePreservedEntityIds = failurePreservedScene!.Asset.Hierarchy.Parts.Keys.ToArray();
                var fixtureRoot = Directory.GetParent(Path.GetDirectoryName(failurePreservedScene.Path)!)!.FullName;
                Assert(fixtureRoot.Contains(Path.Combine("Stride-ImGui-UI", ""), StringComparison.OrdinalIgnoreCase),
                    "Controlled compilation failure must modify only the disposable UI fixture");
                failedLoadSource = Path.Combine(fixtureRoot, "BasicCameraController.cs");
                Assert(File.Exists(failedLoadSource), "The controlled compiler fixture source must exist");
                failedLoadSourceBackup ??= File.ReadAllBytes(failedLoadSource);
                File.AppendAllText(failedLoadSource, "\n#error " + CompileFailureMarker + "\n");
                Next(); break;
            case 10401: if (Click("toolbar/open")) Next(); break;
            case 10402:
                if (Click("project/open"))
                { loadOutputSequence = game.CaptureWorkflowRecentOutput().LastOrDefault()?.Sequence ?? 0; Next(); }
                break;
            case 10403:
                if (TrySaveReplacementPrompt()) break;
                if (game.IsProjectLoading) Next();
                else if (game.CaptureWorkflowCommandFailureAfter(loadOutputSequence) is { } early)
                    throw new InvalidOperationException("Replacement failed before its compiler ran: " + early.Text);
                break;
            case 10404:
                if (game.WorkflowProjectPublicationPending) break;
                Assert(game.CaptureWorkflowRecentOutput(512).Any(entry => entry.Sequence > loadOutputSequence
                    && entry.Text.Contains(CompileFailureMarker, StringComparison.Ordinal)),
                    "Expected failure must come from the actual controlled compiler diagnostic");
                var preservedViewport = game.CaptureInspectionSnapshot().Viewport;
                var missingRuntime = failurePreservedEntityIds.Where(id => !game.HasRuntimeEntity(id,
                    failurePreservedScene!.Asset.Hierarchy.Parts[id].Entity.Transform.Parent?.Entity.Id)).ToArray();
                Assert(ReferenceEquals(game.Document, failurePreservedScene) && !failurePreservedScene!.IsDirty
                    && game.UsesCompiledProjectContent && preservedViewport.HasRuntimeScene && missingRuntime.Length == 0,
                    "A failed replacement compile must retain the original usable authored/runtime session"
                    + "; canonicalSame=" + ReferenceEquals(game.Document, failurePreservedScene)
                    + "; dirty=" + failurePreservedScene!.IsDirty + "; compiled=" + game.UsesCompiledProjectContent
                    + "; viewportPresent=" + preservedViewport.IsPresent + "; runtimeScene=" + preservedViewport.HasRuntimeScene
                    + "; missingRuntime=" + string.Join(',', missingRuntime));
                checks.Add("Actual replacement compiler failure preserves the prior canonical scene and runtime entities");
                if (scenario == UiWorkflowScenario.U07ErrorNavigation) { stage = 11300; break; }
                File.WriteAllBytes(failedLoadSource!, failedLoadSourceBackup!);
                Next(); break;
            case 10405: if (Click("toolbar/open")) Next(); break;
            case 10406:
                if (Click("project/open"))
                { loadOutputSequence = game.CaptureWorkflowRecentOutput().LastOrDefault()?.Sequence ?? 0; Next(); }
                break;
            case 10407:
                if (TrySaveReplacementPrompt()) break;
                if (game.IsProjectLoading) Next();
                else if (game.CaptureWorkflowCommandFailureAfter(loadOutputSequence) is { } retry)
                    throw new InvalidOperationException("Retry failed before publication: " + retry.Text);
                break;
            case 10408:
                if (game.WorkflowProjectPublicationPending) break;
                if (game.CaptureWorkflowCommandFailureAfter(loadOutputSequence) is { } retryFailure)
                    throw new InvalidOperationException("Restored source did not recover the real compiler: " + retryFailure.Text);
                Assert(game.Document != null && !game.Document.IsDirty && game.UsesCompiledProjectContent
                    && game.CaptureInspectionSnapshot().Viewport.HasRuntimeScene
                    && game.Document.Asset.Hierarchy.Parts.Keys.Order().SequenceEqual(failurePreservedEntityIds.Order()),
                    "Restoring source and retrying must publish usable compiled content with the original asset/entity identities");
                Assert(File.ReadAllBytes(failedLoadSource!).SequenceEqual(failedLoadSourceBackup!),
                    "Failure injection must restore the exact original fixture source bytes");
                checks.Add("Restoring fixture source and retrying successfully publishes actual compiled content");
                CompleteIndependentSubset(); break;
        }
    }
}
