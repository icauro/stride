using Stride.Core.IO;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private Func<bool>? pendingRecoveryWorkspaceRestore;
    private Func<bool>? pendingRecoveryWorkspaceCompensation;
    private bool recoveryWorkspaceNativeFocusPending;
    private bool CanActivateOutputAfterRecovery()
    {
        if (IsRecoveryWorkspaceRestorePending) return false;
        if (!recoveryWorkspaceNativeFocusPending) return true;
        if (!NativeDocumentRestorationSettled) return false;
        recoveryWorkspaceNativeFocusPending = false;
        return true;
    }
    private string? recoveryWorkspaceRestoreFailure;
    private bool IsRecoveryWorkspacePresentationRestoring { get; set; }
    internal string? RecoveryWorkspaceRestoreFailure => recoveryWorkspaceRestoreFailure;
    private void CancelRecoveryWorkspaceRestore()
    {
        pendingRecoveryWorkspaceRestore = null;
    }
    private bool TryDrainRecoveryWorkspaceRestore()
        => pendingRecoveryWorkspaceRestore == null && pendingRecoveryWorkspaceCompensation == null;
    private bool PumpRecoveryWorkspaceCompensation()
    {
        if (pendingRecoveryWorkspaceCompensation == null) return pendingRecoveryWorkspaceRestore == null;
        var previousScope = IsRecoveryWorkspacePresentationRestoring;
        bool complete;
        try { IsRecoveryWorkspacePresentationRestoring = true; complete = pendingRecoveryWorkspaceCompensation(); }
        finally { IsRecoveryWorkspacePresentationRestoring = previousScope; }
        if (!complete) return false;
        pendingRecoveryWorkspaceCompensation = null;
        return pendingRecoveryWorkspaceRestore == null;
    }
    private EditorRecoveryWorkspaceState CaptureRecoveryWorkspace()
    {
        var current = workspace ?? throw new InvalidOperationException("No recovery workspace is loaded.");
        var identity = DocumentGroupWorkspaceIdentity!;
        DocumentGroupPanel.Model.Synchronize(documents.Entries);
        var descriptors = documents.Entries.Select(entry =>
        {
            var item = current.AssetItems.FirstOrDefault(item => SamePath(item.FullPath.ToOSPath(), entry.Path));
            var source = entry.Document as ScriptSourceDocument;
            var presentationOwner = source?.GeneratedByAssetId is { } producer
                ? current.AssetItems.SingleOrDefault(candidate => candidate.Id == producer)
                    ?? throw new InvalidOperationException("The generated source has no current native producer asset.")
                : item;
            return new EditorRecoveryOpenDocument(Path.GetFullPath(entry.Path), entry.Document.GetType().FullName!,
                item?.Id.ToString(), presentationOwner?.Package.FullPath.ToOSPath(), presentationOwner?.Package.Container?.AssetNamespace ?? presentationOwner?.Package.AssetNamespace,
                source?.ProjectPath, source?.GeneratedByAssetId?.ToString(), source != null && !source.CanEditText);
        }).ToArray();
        return new EditorRecoveryWorkspaceState(1, current.OpenPath, Gui.SaveIniSettingsToMemoryS(),
            DocumentGroupPanel.CaptureLayout(identity, documents.Active?.Path), descriptors, documents.Active?.Path).Snapshot(current.OpenPath);
    }
    private void QueueRecoveryWorkspaceRestore(EditorRecoveryWorkspaceState state)
    {
        if (pendingRecoveryWorkspaceRestore != null || pendingRecoveryWorkspaceCompensation != null)
            throw new InvalidOperationException("Workspace recovery is already pending.");
        var expectedWorkspace = workspace ?? throw new InvalidOperationException("No recovery workspace is loaded.");
        var expectedHistory = sessionHistory;
        var requestedUtc = DateTime.UtcNow;
        recoveryWorkspaceRestoreFailure = null;
        state = state.Snapshot(expectedWorkspace.OpenPath);
        ValidateRecoveryWorkspaceDependencies(state);
        if (!string.Equals(state.Groups.Workspace, DocumentGroupWorkspaceIdentity, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Recovery layout belongs to another package workspace.");
        if (documents.Entries.Any(entry => !state.Documents.Any(descriptor => SamePath(descriptor.Path, entry.Path))))
            throw new InvalidOperationException("Workspace recovery would omit an existing open document.");
        var previous = CaptureRecoveryWorkspace();
        var retained = documents.Entries.Select(entry => (Entry: entry, Document: entry.Document, State: entry.State, Generation: entry.State?.Generation)).ToArray();
        var added = new List<(EditorDocumentEntry Entry, IEditorDocument Document, EditorAuthoringState? State)>();
        var retiredAddedSceneViewports = new List<EngineViewport>();
        // A rejected request has not acquired presentation owners and needs no compensation.
        foreach (var descriptor in state.Documents) ValidateRecoveryWorkspaceDescriptor(descriptor);
        recoveryWorkspaceNativeFocusPending = true;
        pendingRecoveryWorkspaceCompensation = () =>
        {
            if (!ReferenceEquals(workspace, expectedWorkspace) || !ReferenceEquals(sessionHistory, expectedHistory))
                throw new InvalidOperationException("Recovery compensation lost its retained workspace owner.");
            if (retained.Any(owner => !documents.Entries.Contains(owner.Entry) || !ReferenceEquals(owner.Entry.Document, owner.Document)
                || !ReferenceEquals(owner.Entry.State, owner.State) || owner.Entry.State?.Generation != owner.Generation))
                throw new InvalidOperationException("Recovery compensation cannot replay over a changed retained owner.");
            foreach (var owned in added.ToArray())
            {
                if (!documents.Entries.Contains(owned.Entry)) { added.Remove(owned); continue; }
                if (!ReferenceEquals(owned.Entry.Document, owned.Document) || !ReferenceEquals(owned.Entry.State, owned.State) || owned.Document.IsDirty)
                    throw new InvalidOperationException("Recovery compensation cannot close a changed authored owner.");
                if (sceneDocumentViews.TryGetValue(owned.Entry.Id, out var actualView)
                    && !retiredAddedSceneViewports.Contains(actualView.Viewport)) retiredAddedSceneViewports.Add(actualView.Viewport);
                ReleaseSceneDocumentView(owned.Entry, persistViewSettings: false);
                CloseDocument(owned.Entry);
                added.Remove(owned);
            }
            // Scene CPU shutdown is earlier than native target disposal and the
            // independently certified preview GPU retirement queue. Keep these
            // actual added borrowers until their full resource ownership retires.
            if (IsEditorGpuRetirementPending || IsSceneDocumentViewDraining
                || retiredAddedSceneViewports.Any(viewport => !viewport.IsResourceRetirementComplete)) return false;
            if (!LoadEditorLayoutFromMemory(previous.NativeIni, previous.Groups))
                throw new InvalidOperationException("Recovery compensation could not restore retained native presentation.");
            RestoreCurrentDocumentGroupLayout();
            if (previous.ActivePath != null && documents.Find(previous.ActivePath) is { } active) ActivateDocument(active);
            return true;
        };
        pendingRecoveryWorkspaceRestore = () =>
        {
            if (!ReferenceEquals(workspace, expectedWorkspace) || !ReferenceEquals(sessionHistory, expectedHistory))
                throw new InvalidOperationException("The recovery workspace owner changed.");
            if (!string.Equals(state.Groups.Workspace, DocumentGroupWorkspaceIdentity, StringComparison.OrdinalIgnoreCase)
                || retained.Any(owner => !documents.Entries.Contains(owner.Entry) || !ReferenceEquals(owner.Entry.Document, owner.Document)
                    || !ReferenceEquals(owner.Entry.State, owner.State) || owner.Entry.State?.Generation != owner.Generation)
                || added.Any(owner => !documents.Entries.Contains(owner.Entry) || !ReferenceEquals(owner.Entry.Document, owner.Document)
                    || !ReferenceEquals(owner.Entry.State, owner.State))
                || documents.Entries.Any(entry => !retained.Any(owner => ReferenceEquals(owner.Entry, entry)) && !added.Any(owner => ReferenceEquals(owner.Entry, entry))))
                throw new InvalidOperationException("The retained recovery document generation changed.");
            if (DateTime.UtcNow - requestedUtc > TimeSpan.FromMinutes(2))
                throw new TimeoutException("The recovered native document presentation did not become ready.");
            foreach (var descriptor in state.Documents) ValidateRecoveryWorkspaceDescriptor(descriptor);
            ValidateRecoveryWorkspaceDependencies(state);
            var ready = true;
            foreach (var descriptor in state.Documents)
            {
                if (documents.Find(descriptor.Path) == null)
                {
                    try
                    {
                    if (descriptor.Kind == typeof(SceneDocument).FullName) OpenScene(descriptor.Path);
                    else if (descriptor.Kind == typeof(PrefabDocument).FullName) OpenPrefab(descriptor.Path);
                    else if (descriptor.Kind == typeof(ProceduralAssetDocument).FullName) OpenAssetEditor(descriptor.Path);
                    else if (descriptor.Kind == typeof(ScriptSourceDocument).FullName || descriptor.Kind == typeof(AssetScriptSourceDocument).FullName)
                        OpenRecoverySourcePresentation(descriptor);
                    else
                    {
                        var item = workspace!.FindAsset(descriptor.Path);
                        GetAssetTagDocument(new AssetEntry(descriptor.Path, item.UnqualifiedUrl.ToString(), Path.GetExtension(descriptor.Path),
                            item.Asset.GetType(), item.Id, item.Asset.Tags.ToArray()), false);
                    }
                    }
                    finally
                    {
                        foreach (var opened in documents.Entries.Where(entry => !retained.Any(owner => ReferenceEquals(owner.Entry, entry))
                            && !added.Any(owner => ReferenceEquals(owner.Entry, entry))))
                            added.Add((opened, opened.Document, opened.State));
                    }
                }
                var entry = documents.Find(descriptor.Path) ?? throw new InvalidOperationException("The recovered document did not acquire its native owner.");
                if (entry.Document.GetType().FullName != descriptor.Kind)
                    throw new InvalidOperationException("The recovered document has another native editor kind.");
                if (added.Any(owner => ReferenceEquals(owner.Entry, entry)))
                    InjectRecoveryWorkspaceCompensationFailure(entry);
                if (entry.Document is EntityHierarchyDocument ? !CanPresentSceneDocument(entry) : !CanPresentNonSceneDocument(entry)) ready = false;
            }
            if (!ready) return false; // Normal owner-frame compilation/presentation pumps continue between attempts.
            // Never silently omit current documents from a restored group topology.
            if (documents.Entries.Any(entry => !state.Documents.Any(descriptor => SamePath(descriptor.Path, entry.Path))))
                throw new InvalidOperationException("Workspace recovery would omit an existing open document.");
            if (!LoadEditorLayoutFromMemory(state.NativeIni, state.Groups))
                throw new InvalidOperationException("The recovered native layout could not be loaded.");
            RestoreCurrentDocumentGroupLayout();
            if (state.ActivePath != null) ActivateDocument(documents.Find(state.ActivePath)!);
            pendingRecoveryWorkspaceCompensation = null;
            return true;
        };
    }
    private void ValidateRecoveryWorkspaceDescriptor(EditorRecoveryOpenDocument descriptor)
    {
        var current = workspace!;
        var supported = new[] { typeof(SceneDocument), typeof(PrefabDocument), typeof(ProceduralAssetDocument),
            typeof(ScriptSourceDocument), typeof(AssetScriptSourceDocument), typeof(VisualScriptDocument), typeof(UIDocument), typeof(AssetMetadataDocument) };
        if (!supported.Any(type => type.FullName == descriptor.Kind))
            throw new InvalidOperationException("The recovery document editor kind is not supported.");
        if (IsUnownedReadonlyRecoverySource(descriptor))
        {
            ValidateUnownedReadonlyRecoverySource(descriptor);
            return;
        }
        if (descriptor.GeneratedByAssetId != null)
        {
            ValidateRecoveryGeneratedSourceMembership(descriptor);
            return;
        }
        var item = current.AssetItems.FirstOrDefault(item => SamePath(item.FullPath.ToOSPath(), descriptor.Path));
        if (item != null)
        {
            if (descriptor.AssetId != item.Id.ToString() || descriptor.PackagePath == null
                || !SamePath(descriptor.PackagePath, item.Package.FullPath.ToOSPath())
                || descriptor.Namespace != (item.Package.Container?.AssetNamespace ?? item.Package.AssetNamespace))
                throw new InvalidOperationException("The recovered document belongs to another canonical package asset.");
        }
        else if (descriptor.AssetId != null || descriptor.PackagePath != null || descriptor.Namespace != null)
            throw new InvalidOperationException("The recovered canonical asset is missing.");
        if (descriptor.Kind == typeof(ScriptSourceDocument).FullName || descriptor.Kind == typeof(AssetScriptSourceDocument).FullName)
        {
            ValidateRecoverySourceMembership(descriptor);
        }
        else if (item == null) throw new InvalidOperationException("The recovered native asset is not loaded.");
        if (documents.Find(descriptor.Path) is { } existing && existing.Document.GetType().FullName != descriptor.Kind)
            throw new InvalidOperationException("The existing document has another canonical editor kind.");
    }
    private void PumpRecoveryWorkspaceRestoreBeforeFrame()
    {
        if (pendingRecoveryWorkspaceRestore == null && pendingRecoveryWorkspaceCompensation != null)
        {
            try { PumpRecoveryWorkspaceCompensation(); }
            catch (Exception error) { recoveryWorkspaceRestoreFailure = error.Message; statusMessage = error.Message; }
            return;
        }
        if (pendingRecoveryWorkspaceRestore == null || RecoveryTransitionBusy || projectLoading != null
            || IsAuthoringPipelineUpdating || IsAssetPreviewUpdating || IsEditorGpuRetirementPending || IsEditorGraphicsRecovering) return;
        var action = pendingRecoveryWorkspaceRestore;
        try { IsRecoveryWorkspacePresentationRestoring = true; if (action()) pendingRecoveryWorkspaceRestore = null; }
        catch (Exception error) { pendingRecoveryWorkspaceRestore = null; recoveryWorkspaceRestoreFailure = error.Message; statusMessage = error.Message; RecordAssetPreviewRenderFailure("recovery", error); }
        finally { IsRecoveryWorkspacePresentationRestoring = false; }
    }
}
