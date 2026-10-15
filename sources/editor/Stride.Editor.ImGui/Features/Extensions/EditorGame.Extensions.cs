using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Diagnostics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool editorExtensionsStopping;
    private string? extensionFailure;
    private void InitializeEditorExtensions(ProjectWorkspace current)
    {
        if (sessionHistory == null || authoring == null) throw new InvalidOperationException("Initialize canonical session authoring before extensions.");
        if (sessionHistory.Extensions != null) throw new InvalidOperationException("The editor extension session is already initialized.");
        editorExtensionsStopping = false; extensionFailure = null;
        var history = sessionHistory;
        (IAssetTagDocument Document, AssetPropertyGraph Graph, Action<Action, string> Mutate) Resolve(AssetId id)
        {
            if (!ReferenceEquals(workspace, current) || !ReferenceEquals(sessionHistory, history)) throw new InvalidOperationException("The extension belongs to a different editor session.");
            if (history.Registry.Find(id)?.Document is EntityHierarchyDocument retained)
                return (retained, retained.HierarchyGraph, retained.ApplyInheritedGraphMutation);
            var item = current.Session.FindAsset(id) ?? throw new InvalidOperationException("The extension asset is not part of the actual session.");
            if (item.Asset is Stride.Assets.Entities.EntityHierarchyAssetBase)
            {
                var hierarchy = GetCanonicalDocument(item.FullPath.ToOSPath(), () => item.Asset is Stride.Assets.Entities.SceneAsset
                    ? new SceneDocument(item.FullPath.ToOSPath(), history, item.Package.IsReadOnly)
                    : item.Asset is Stride.Assets.Entities.PrefabAsset ? new PrefabDocument(item.FullPath.ToOSPath(), history, item.Package.IsReadOnly)
                    : throw new InvalidOperationException("This hierarchy asset has no canonical editor document.")) as EntityHierarchyDocument
                    ?? throw new InvalidOperationException("The hierarchy asset already has an incompatible canonical owner.");
                return (hierarchy, hierarchy.HierarchyGraph, hierarchy.ApplyInheritedGraphMutation);
            }
            var document = history.AssetGraphs.Resolve?.Invoke(id) ?? throw new InvalidOperationException("This asset has no supported canonical property graph.");
            return (document, document.AuthoringGraph, document.ApplyInheritedAssetMutation);
        }
        var context = new EditorExtensionContext(current.Session, GlobalLogger.GetLogger("EditorExtensions"), id => Resolve(id).Graph,
            (id, mutate, title) =>
            {
                var commandActor = extensionCommand is { } run && ReferenceEquals(SynchronizationContext.Current, run.Actor)
                    && !run.Cancellation.IsCancellationRequested && ReferenceEquals(run.History, history)
                    && run.Owner is IAssetTagDocument tagged && tagged.AuthoredAsset.Id == id;
                if (editorExtensionsStopping || projectLoading != null || assetPreviewBuild != null
                    || IsAuthoringPipelineUpdatingExceptEditorExtensionCommand || IsEditorExtensionCommandRunning && !commandActor
                    || closeRequested || closePromptOpen || closingDocument != null)
                    throw new InvalidOperationException("The editor is busy or closing.");
                var document = Resolve(id);
                if (!document.Document.CanSave || history.Registry.Find(id) is not { IsPublished: true } owner || !ReferenceEquals(owner.Document, document.Document))
                    throw new InvalidOperationException("The asset has no editable canonical owner.");
                var nestedNativeMutation = history.IsCapturingOperations && (document.Document switch
                {
                    AssetMetadataDocument metadata => metadata.HasActiveNativeMutationJournal,
                    EntityHierarchyDocument hierarchy => hierarchy.HasActiveNativeMutationJournal,
                    _ => false,
                });
                if (commandActor || nestedNativeMutation)
                {
                    if (commandActor && !ReferenceEquals(extensionCommand!.Owner, document.Document)) throw new InvalidOperationException("The command no longer owns this canonical graph.");
                    mutate(document.Graph);
                }
                else document.Mutate(() => mutate(document.Graph), title);
            }, (action, title) =>
            {
                using var capture = history.CaptureOperationsAtomic(action);
                capture.Publish(history, title);
            }, editorOwner: id =>
            {
                if (!ReferenceEquals(workspace, current) || !ReferenceEquals(sessionHistory, history))
                    throw new InvalidOperationException("The editor belongs to a different canonical session.");
                return history.Registry.Find(id) ?? throw new InvalidOperationException("The asset has no canonical editor owner.");
            });
        history.Extensions = new(context);
    }
    private void PumpEditorExtensions()
    {
        if (sessionHistory?.Extensions == null || editorExtensionsStopping || projectLoading != null || assetPreviewBuild != null
            || IsAuthoringPipelineUpdating || closeRequested || closePromptOpen || closingDocument != null) return;
        try { sessionHistory.Extensions.Refresh(); extensionFailure = null; }
        catch (Exception error)
        {
            var message = error.ToString();
            if (extensionFailure != message) { extensionFailure = message; GlobalLogger.GetLogger("EditorExtensions").Error("Editor extension initialization or cleanup failed.", error); }
        }
    }
    private bool TryOpenRegisteredAssetEditor(AssetEntry entry)
    {
        var extensions = sessionHistory?.Extensions;
        if (workspace == null || extensions == null || !extensions.HasAssetEditor(entry.AssetType)) return false;
        if (editorExtensionsStopping || IsAuthoringPipelineUpdating) throw new InvalidOperationException("The editor is busy or closing.");
        var canonical = GetAssetTagDocument(entry, false);
        var item = workspace.FindAsset(entry.Path).Clone(keepPackage: true, newAsset: canonical.AuthoredAsset);
        return extensions.TryOpenAssetEditor(item);
    }
    private void StopEditorExtensions()
    {
        DrainEditorExtensionCommands();
        editorExtensionsStopping = true; sessionHistory?.Extensions?.Stop();
    }
    private void DisposeEditorExtensions()
    {
        if (sessionHistory?.Extensions is not { } extensions) return;
        StopEditorExtensions();
        extensions.DrainOwnedAssetEditors();
        icons?.ClearCustomComponentImages();
        // Providers can read the real session during disposal; their graphs and
        // package models must outlive this callback, while authoring is stopped.
        foreach (var state in authoring!.RetainedStates)
            if (state.Document is EntityHierarchyDocument hierarchy) hierarchy.DisposePropertyPresenters();
            else if (state.Document is AssetMetadataDocument metadata) metadata.DisposeExtensionAssetPresentation();
            else if (state.Document is ProceduralAssetDocument procedural) procedural.DisposeNumericPresentation();
        extensions.Dispose(); sessionHistory.Extensions = null; extensionFailure = null;
    }
}
