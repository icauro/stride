using Stride.Core.Diagnostics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record SceneLoadVerificationFault(EngineViewport View, string Path, string AssetId, string MissingUrl);
    private SceneLoadVerificationFault? sceneLoadVerificationFault;
    private object? sceneLoadVerificationReceipt;

    // No arm means no URL change or snapshot allocation in the normal editor.
    internal string? ConsumeSceneLoadVerificationUrl(EngineViewport view, EntityHierarchyDocument owner, string actualUrl)
    {
        var fault = sceneLoadVerificationFault;
        if (fault == null || !ReferenceEquals(fault.View, view)) return null;
        if (!Environment.GetCommandLineArgs().Contains("--ui-test")
            || !string.Equals(fault.Path, owner.Path, StringComparison.OrdinalIgnoreCase)
            || fault.AssetId != owner.Asset.Id.ToString())
            throw new InvalidOperationException("The diagnostic native scene-load owner changed.");
        // Consume before ContentManager.Load: the actual compensating load must
        // receive the original retained bundle URL, never this missing URL.
        sceneLoadVerificationFault = null;
        sceneLoadVerificationReceipt = new { fault.MissingUrl, actualUrl, assetId = fault.AssetId,
            ownedPath = fault.Path, consumedBeforeNativeLoad = true };
        return fault.MissingUrl;
    }

    private object VerifySceneLoadCompensationParity(SceneGenerationCase failed, SceneGenerationCase unaffected)
    {
        var privateRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
        if (!Environment.GetCommandLineArgs().Contains("--ui-test") || sessionHistory == null
            || !Path.GetFullPath(failed.Entry.Path).StartsWith(privateRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Native load failure requires an owned private fixture.");
        if (!sceneDocumentViews.TryGetValue(failed.Entry.Id, out var beforeView) || !IsCurrentSceneDocumentView(beforeView)
            || beforeView.OwnerRebind != null || failed.View.CompiledInputs is not { } inputs)
            throw new InvalidOperationException("The failing view must have certified valid retained compiled inputs.");
        var otherRuntime = unaffected.View.RuntimeScene; var otherGame = unaffected.View.RuntimeGame;
        var otherTarget = unaffected.View.Target;
        var originalTarget = failed.View.Target;
        var width = originalTarget?.Width ?? 0; var height = originalTarget?.Height ?? 0;
        var history = VerificationHistoryTransactions.ToArray();
        var missingUrl = "/__OwnedSceneLoadFailure_" + Guid.NewGuid().ToString("N");
        if (Content.FileProvider.ObjectDatabase.ContentIndexMap.Contains(missingUrl))
            throw new InvalidOperationException("The injected compiled URL must actually be absent.");
        var snapshot = failed.Before.CaptureAssemblyReload();
        sceneLoadVerificationReceipt = null;
        sceneLoadVerificationFault = new(failed.View, failed.Entry.Path, failed.Before.Asset.Id.ToString(), missingUrl);
        Exception? failure = null;
        try
        {
            ReplaceAuthoredDocument(failed.Entry,
                () => failed.Before.CreateAssemblyReloadedGeneration(snapshot, new LoggerResult()),
                "Verify actual native scene-load compensation");
        }
        catch (Exception error) { failure = error; }
        finally { sceneLoadVerificationFault = null; }
        if (failure == null || sceneLoadVerificationReceipt == null
            || !failure.ToString().Contains(missingUrl, StringComparison.Ordinal)
            || !failure.ToString().Contains("ContentManager", StringComparison.Ordinal))
            throw new InvalidOperationException("The real native ContentManager must fail at the missing compiled scene URL.", failure);
        if (!ReferenceEquals(failed.Entry.State, failed.BeforeState) || !ReferenceEquals(failed.Entry.Document, failed.Before)
            || !sceneDocumentViews.TryGetValue(failed.Entry.Id, out var restored) || !IsCurrentSceneDocumentView(restored)
            || !ReferenceEquals(restored.Viewport, failed.View) || !ReferenceEquals(restored.Document, failed.Before)
            || !ReferenceEquals(restored.State, failed.BeforeState) || restored.OwnerRebind != null
            || !ReferenceEquals(failed.View.PresentedDocument, failed.Before) || failed.View.RuntimeScene == null
            || failed.View.CompiledInputs != inputs || failed.View.Camera.Component.ViewMatrix != failed.Camera
            || failed.View.Target is not { } target || target.Width != width || target.Height != height
            || !failed.Entities.SequenceEqual(failed.Before.Asset.Hierarchy.Parts.Keys.Order())
            || !failed.Selection.SequenceEqual(failed.Before.SelectedIds) || failed.Before.IsDirty != failed.Dirty
            || !failed.Disk.SequenceEqual(File.ReadAllBytes(failed.Entry.Path))
            || !history.SequenceEqual(VerificationHistoryTransactions))
            throw new InvalidOperationException("Failed canonical load did not restore actual owner/view/inputs/camera/resources/history.", failure);
        if (!sceneDocumentViews.TryGetValue(unaffected.Entry.Id, out var otherView) || !IsCurrentSceneDocumentView(otherView)
            || !ReferenceEquals(unaffected.Entry.State, unaffected.BeforeState)
            || !ReferenceEquals(unaffected.View.RuntimeScene, otherRuntime) || !ReferenceEquals(unaffected.View.RuntimeGame, otherGame)
            || !ReferenceEquals(unaffected.View.Target, otherTarget) || unaffected.View.Camera.Component.ViewMatrix != unaffected.Camera
            || !unaffected.Selection.SequenceEqual(unaffected.Before.SelectedIds)
            || !unaffected.Disk.SequenceEqual(File.ReadAllBytes(unaffected.Entry.Path)))
            throw new InvalidOperationException("A failed native load affected the independent scene view.", failure);
        return new { actualNativeFailure = failure.GetType().FullName, receipt = sceneLoadVerificationReceipt,
            realCanonicalCompensation = true, retainedValidInputsRestored = true, recoveryLeaseReleased = true,
            independentViewUnchanged = true, nativeTargetExtentRestored = true, unchangedDiskAndHistory = true,
            terminalRetirementRequired = true };
    }
}
