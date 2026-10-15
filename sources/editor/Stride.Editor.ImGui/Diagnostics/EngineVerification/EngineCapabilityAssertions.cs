using Stride.Engine;

namespace Stride.Editor.ImGui;

/// <summary>Engine assertions combine real compiled resources, authored/runtime identities and GPU output.</summary>
internal static class EngineCapabilityAssertions
{
    internal static void CompiledScene(EngineViewport viewport, EntityHierarchyDocument document, ProjectWorkspace workspace)
    {
        Require(viewport.RuntimeSourceAvailable, "E01", "The actual runtime source is unavailable.");
        Require(viewport.UsesAuthoredCompositor, "E01", "The fixture's compiled compositor is not installed.");
        var evidence = viewport.InspectImportedFixture(document, workspace);
        Require(evidence.FixturePresent && evidence.SourceIsImportedModel, "E01", "The expected imported fixture is absent.");
        Require(evidence.Meshes > 0 && evidence.VertexBufferBytes > 0 && evidence.IndexBufferBytes > 0
            && evidence.MaterialPasses > 0, "E01", $"Compiled fixture GPU resources are incomplete: {evidence}.");
        Require(viewport.Target is { Width: > 0, Height: > 0 }, "E01", "There is no real scene output target.");
    }

    internal static void RuntimeEntity(EngineViewport viewport, Entity source, Guid? expectedParentId)
    {
        Require(viewport.HasRuntimeEntity(source.Id, expectedParentId), "E01", $"Runtime entity {source.Id} has wrong ownership/parent.");
        Require(viewport.MatchesTransform(source), "E01", $"Runtime transform differs from authored entity {source.Id}.");
        Require(viewport.MatchesWorldTransform(source), "E01", $"Runtime world transform differs from authored entity {source.Id}.");
    }

    internal static void VisibleFixtureChange(EngineOutputDifference difference, int minimumChangedPixels, double maximumChangedFraction)
    {
        if (minimumChangedPixels <= 0 || minimumChangedPixels > difference.Pixels)
            throw new ArgumentOutOfRangeException(nameof(minimumChangedPixels));
        if (!double.IsFinite(maximumChangedFraction) || maximumChangedFraction is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(maximumChangedFraction));
        Require(difference.ChangedPixels >= minimumChangedPixels, "E01/E03/E04",
            $"The fixture edit did not visibly affect its GPU output region: {difference}.");
        Require(difference.ChangedFraction <= maximumChangedFraction, "E01/E03/E04",
            $"Output changed outside the fixture's expected budget: {difference}. Stabilize exposure/camera/animation first.");
    }

    internal static void RetiredScene(EngineViewport viewport)
    {
        Require(viewport.IsSceneRetirementDrainComplete, "E06", "Actual scene workers are still draining.");
        Require(viewport.IsResourceRetirementComplete, "E06", "Scene/output resource owners are not actually retired.");
        Require(viewport.RuntimeScene == null && viewport.Target == null, "E06", "A retired viewport retains a runtime scene or output target.");
    }

    private static void Require(bool condition, string scenario, string message)
    { if (!condition) throw new InvalidOperationException($"{scenario}: {message}"); }
}
