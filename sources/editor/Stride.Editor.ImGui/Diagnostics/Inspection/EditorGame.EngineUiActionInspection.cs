using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private object? verificationAssetUpdateButton;
    private object? verificationAssetUpdateActivation;
    private Task<string>? verificationStartedAssetUpdate;
    private string? verificationStartedAssetPath;
    private long verificationAssetUpdateActivationSequence;
    private long verificationAssetUpdateStartSequence;
    internal long VerificationAssetUpdateActivationSequence => verificationAssetUpdateActivationSequence;
    internal long VerificationAssetUpdateStartSequence => verificationAssetUpdateStartSequence;
    internal Task<string>? VerificationStartedAssetUpdateTask => verificationStartedAssetUpdate;
    internal bool VerificationAssetUpdatePipelineBusy => assetPreviewBuild != null || projectLoading != null
        || IsAuthoringPipelineUpdating || IsUserProjectBuilding;
    internal ModelAssetPreview? VerificationAssetUpdateActivatedPreview { get; private set; }
    internal SceneDocument? ResolveEngineSceneEntry(Guid? entryId, string path, string assetId)
    {
        var entry = documents.Entries.FirstOrDefault(candidate => candidate.Id == entryId);
        return entry?.Document is SceneDocument current
            && string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase)
            && current.Asset.Id.ToString() == assetId && IsCurrentDocumentPresentation(entry)
            && sceneDocumentViews.TryGetValue(entry.Id, out var view) && IsCurrentSceneDocumentView(view)
            && ReferenceEquals(view.Viewport.PresentedDocument, current) ? current : null;
    }
    internal object InspectEngineSceneEntryCertificate(Guid? entryId, SceneDocument? previous, EngineViewport? expected)
    {
        var entry = documents.Entries.FirstOrDefault(candidate => candidate.Id == entryId);
        var current = entry?.Document as SceneDocument;
        var view = entry != null && sceneDocumentViews.TryGetValue(entry.Id, out var actual) ? actual : null;
        return new
        {
            requestedEntryId = entryId, entryPresent = entry != null, entryId = entry?.Id,
            entryPath = entry?.Path, previousPath = previous?.Path,
            previousAssetId = previous?.Asset.Id.ToString(), currentAssetId = current?.Asset.Id.ToString(),
            documentChanged = current != null && !ReferenceEquals(current, previous),
            currentPresentation = entry != null && IsCurrentDocumentPresentation(entry),
            currentStatePublished = entry?.State?.IsPublished, currentStateDisposed = entry?.State?.IsDisposed,
            currentView = view != null && IsCurrentSceneDocumentView(view),
            sameViewport = view != null && ReferenceEquals(view.Viewport, expected),
            viewDocumentMatchesCurrent = view != null && ReferenceEquals(view.Document, current),
            viewDocumentMatchesPrevious = view != null && ReferenceEquals(view.Document, previous),
            viewStateMatchesCurrent = view != null && ReferenceEquals(view.State, entry?.State),
            viewStatePublished = view?.State.IsPublished, viewStateDisposed = view?.State.IsDisposed,
            viewWorkspaceMatches = view != null && ReferenceEquals(view.Workspace, workspace),
            viewProjectContentMatches = view != null && ReferenceEquals(view.ProjectContent, projectContent),
            viewSourceContentMatches = view != null && ReferenceEquals(view.Viewport.SourceContent, view.Content),
            presentedCurrentDocument = view != null && ReferenceEquals(view.Viewport.PresentedDocument, current),
            currentRuntimePresent = view?.Viewport.RuntimeScene != null
        };
    }
    internal object InspectEngineSceneOwnerCertificate(SceneDocument? owner, EngineViewport? expected)
    {
        var entry = owner == null ? null : documents.Find(owner);
        var view = entry != null && sceneDocumentViews.TryGetValue(entry.Id, out var actual) ? actual : null;
        return new
        {
            entryId = entry?.Id, entryPresent = entry != null,
            entryDocumentMatches = entry != null && ReferenceEquals(entry.Document, owner),
            entryLeasePresent = entry?.ViewLease != null,
            statePublished = entry?.State?.IsPublished, stateDisposed = entry?.State?.IsDisposed,
            currentPresentation = entry != null && IsCurrentDocumentPresentation(entry),
            viewPresent = view != null, sameViewport = view != null && ReferenceEquals(view.Viewport, expected),
            expectedRuntimePresent = expected?.RuntimeScene != null,
            expectedPresentedOwner = expected != null && ReferenceEquals(expected.PresentedDocument, owner),
            currentView = view != null && IsCurrentSceneDocumentView(view),
            viewEntryMatches = view != null && ReferenceEquals(view.Entry, entry),
            viewStateMatches = view != null && ReferenceEquals(view.State, entry?.State),
            viewWorkspaceMatches = view != null && ReferenceEquals(view.Workspace, workspace),
            projectContentMatches = view != null && ReferenceEquals(view.ProjectContent, projectContent),
            sourceContentMatches = view != null && ReferenceEquals(view.Viewport.SourceContent, view.Content),
            presentedDocumentMatches = view != null && ReferenceEquals(view.Viewport.PresentedDocument, owner),
            currentRuntimePresent = view?.Viewport.RuntimeScene != null,
            drainingExpected = expected != null && drainingSceneDocumentViews.Any(candidate => ReferenceEquals(candidate.Viewport, expected))
        };
    }
    private readonly Queue<object> verificationModelPreviewOpenings = new();
    internal object[] VerificationModelPreviewOpenings => verificationModelPreviewOpenings.ToArray();
    private void RecordEngineModelPreviewOpening(AssetEntry entry)
    {
        if (!Controls.Enabled) return;
        verificationModelPreviewOpenings.Enqueue(new
        {
            frame = Gui.GetFrameCount(), entry.Id, entry.Path,
            existingName = modelPreview?.Name, existingDisposed = modelPreview?.IsDisposed,
            assetPreviewPath, compilation = assetPreviewBuild?.Status.ToString(),
            activationSequence = verificationAssetUpdateActivationSequence,
            startSequence = verificationAssetUpdateStartSequence,
            caller = Environment.StackTrace
        });
        while (verificationModelPreviewOpenings.Count > 4) verificationModelPreviewOpenings.Dequeue();
    }
    internal object? VerificationAssetUpdateButton => verificationAssetUpdateButton;
    internal object? VerificationAssetUpdateActivation => verificationAssetUpdateActivation;
    internal object? VerificationAssetUpdateStarted => verificationStartedAssetUpdate == null ? null : new
    {
        taskId = verificationStartedAssetUpdate.Id, status = verificationStartedAssetUpdate.Status.ToString(),
        assetPath = verificationStartedAssetPath,
        currentTask = ReferenceEquals(verificationStartedAssetUpdate, assetPreviewBuild),
        error = verificationStartedAssetUpdate.Exception?.ToString()
    };
    private void RecordEngineAssetUpdateStarted(Task<string> task, string path)
    {
        if (!Controls.Enabled) return;
        verificationStartedAssetUpdate = task;
        verificationStartedAssetPath = path;
        verificationAssetUpdateStartSequence++;
    }

    // Read the actual just-drawn item; retain one current and one activated snapshot.
    private unsafe void RecordEngineAssetUpdateButton(bool pressed)
    {
        if (!Controls.Enabled) return;
        var context = Gui.GetCurrentContext();
        var window = ImGuiP.GetCurrentWindowRead();
        var io = Gui.GetIO();
        var minimum = Gui.GetItemRectMin(); var maximum = Gui.GetItemRectMax();
        var snapshot = new
        {
            frame = Gui.GetFrameCount(), id = context.LastItemData.ID, pressed,
            hovered = Gui.IsItemHovered(), active = Gui.IsItemActive(),
            disabled = (ImGuiP.GetItemFlags() & (ImGuiItemFlags)ImGuiItemFlagsPrivate.Disabled) != 0,
            itemFlags = (int)context.LastItemData.ItemFlags,
            windowId = window.IsNull ? 0u : window.ID,
            activeId = context.ActiveId, hoveredId = context.HoveredId,
            mouse = new { x = io.MousePos.X, y = io.MousePos.Y, leftDown = io.MouseDown[0] },
            rectangle = new { minX = minimum.X, minY = minimum.Y, maxX = maximum.X, maxY = maximum.Y },
            assetPath = assetDocument?.Path, canSave = assetDocument?.CanSave,
            assetBuild = assetPreviewBuild?.Status.ToString(), projectLoading = projectLoading != null,
            authoringBusy = IsAuthoringPipelineUpdating
        };
        verificationAssetUpdateButton = snapshot;
        if (pressed)
        {
            VerificationAssetUpdateActivatedPreview = modelPreview;
            verificationAssetUpdateActivation = snapshot;
            verificationAssetUpdateActivationSequence++;
        }
    }
}
