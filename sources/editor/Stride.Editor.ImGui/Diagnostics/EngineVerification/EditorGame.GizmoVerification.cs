using Stride.Engine.Design;

namespace Stride.Editor.ImGui;

internal sealed record StalePickGenerationVerification(EntityHierarchyDocument PreviousOwner, EntityHierarchyDocument NextOwner,
    Guid PreviousGeneration, Guid NextGeneration, EditorPickingRenderer PreviousPicker, int Readbacks, int Cancellations, Guid? NextSelection);

internal sealed partial class EditorGame
{
    internal SceneTransformGizmos? VerificationTransformGizmos => InspectionViewport?.Gizmos;

    // A service integration fault boundary: queue genuine native work, then use
    // the product's authored reload operation before another frame can consume it.
    internal StalePickGenerationVerification QueuePickAndReloadGenerationForVerification()
    {
        var previous = document ?? throw new InvalidOperationException("A scene document is required.");
        var fixtureRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(previous.Path).StartsWith(fixtureRoot, StringComparison.OrdinalIgnoreCase) || previous.IsDirty)
            throw new InvalidOperationException("Generation verification requires a clean disposable scene fixture.");
        var viewport = InspectionViewport ?? throw new InvalidOperationException("Native viewport unavailable.");
        var picker = viewport.Picker ?? throw new InvalidOperationException("Native picker unavailable.");
        var previousGeneration = authoring!.Find(previous.Asset.Id)!.Generation;
        var reads = picker.ReadbackCount; var canceled = picker.CancelledRequests;
        picker.Request(viewport.EntityScreenPosition("Sphere"), picker.DisplayedViewProjection, picker.DisplayedSize);
        if (!picker.IsPending) throw new InvalidOperationException("Actual native pick request was not queued.");
        pickDocument = previous;
        OpenScene(previous.Path, reload: true);
        var next = document ?? throw new InvalidOperationException("Reload did not publish a document.");
        var nextGeneration = authoring.Find(next.Asset.Id)!.Generation;
        return new(previous, next, previousGeneration, nextGeneration, picker, reads, canceled, next.Selected?.Id);
    }
}

internal sealed partial class SceneTransformGizmos
{
    // Read the actual shared native gizmo hit result computed by its Update.
    internal bool VerificationHandleHovered => Active.IsUnderMouse();
    internal string VerificationHandleAxes => Active.TransformationAxes.ToString();
}
