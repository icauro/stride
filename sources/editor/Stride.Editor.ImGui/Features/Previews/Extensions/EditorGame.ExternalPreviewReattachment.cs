using Stride.Core.Assets;
using Stride.Editor.Preview;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class ExternalPreviewReattachment(ExternalPreviewOwner owner, object? state)
    {
        internal readonly ProjectWorkspace Workspace = owner.Workspace;
        internal readonly AssetId Asset = owner.Entry.Id;
        internal readonly Type AssetType = owner.Entry.AssetType;
        internal readonly Type Provider = owner.ProviderLease.ProviderType;
        internal readonly Type? Adapter = owner.ProviderLease.AdapterType;
        internal readonly object? ViewState = state;
        internal long CheckedVersion = -1;
    }
    private ExternalPreviewReattachment? externalPreviewReattachment;
    private ExternalPreviewReattachment? externalPreviewReattachmentBuild;

    private bool AcceptExternalPreviewReattachment(AssetEntry entry, NativeAssetPreviewRegistry.Lease lease)
    {
        if (externalPreviewReattachmentBuild is not { } pending) return true;
        externalPreviewReattachmentBuild = null;
        var valid = ReferenceEquals(pending.Workspace, workspace) && entry.Id == pending.Asset
            && entry.AssetType == pending.AssetType && lease.ProviderType == pending.Provider && lease.AdapterType == pending.Adapter
            && string.Equals(entry.Path, selectedAssetPath, StringComparison.OrdinalIgnoreCase)
            && (!assetBrowser.HasSelectionInteraction || assetBrowser.SelectedCount == 1 && assetBrowser.PrimaryEntry?.Id == pending.Asset);
        if (!valid) externalPreviewRestoreState = null;
        return valid;
    }

    private bool QueueExternalPreviewReattachment(ExternalPreviewOwner owner)
    {
        if (owner.ProviderLease.IsCoreUnloading || !ReferenceEquals(owner.Workspace, workspace)
            || !string.Equals(owner.Entry.Path, selectedAssetPath, StringComparison.OrdinalIgnoreCase)
            || IsEditorGpuRetirementPending || IsEditorGraphicsRecovering || RecoveryTransitionBusy || closeRequested || exitApproved)
            return false;
        object? state = null;
        if (owner.Initialization?.IsCompleted != false && owner.Runtime is IAssetPreviewViewStateRuntime view
            && !EditorExtensionGenerations.IsUnloading(view.GetType().Assembly))
            state = CaptureExternalPreviewViewState(view);
        externalPreviewReattachment = new(owner, state);
        // An installed source revision is not a surface-generation certificate.
        if (requestedCatalogExternal && requestedCatalogPreview?.Id == owner.Entry.Id) StopCatalogPreviewUpdate();
        return true;
    }

    private void PumpExternalPreviewReattachment()
    {
        if (externalPreviewReattachment is not { } pending) return;
        if (!ReferenceEquals(pending.Workspace, workspace)
            || assetBrowser.HasSelectionInteraction && (assetBrowser.SelectedCount != 1 || assetBrowser.PrimaryEntry?.Id != pending.Asset))
        { externalPreviewReattachment = null; return; }
        AssetEntry? entry = null;
        foreach (var candidate in pending.Workspace.Assets)
            if (candidate.Id == pending.Asset) { entry = candidate; break; }
        if (entry == null || entry.AssetType != pending.AssetType
            || !string.Equals(entry.Path, selectedAssetPath, StringComparison.OrdinalIgnoreCase)
            || EditorExtensionGenerations.IsUnloading(pending.Provider.Assembly)
            || pending.Adapter != null && EditorExtensionGenerations.IsUnloading(pending.Adapter.Assembly))
        { externalPreviewReattachment = null; return; }
        if (externalPreview != null || IsAuthoringPipelineUpdating || projectLoading != null || assetPreviewBuild != null
            || IsUserProjectBuilding || pumpingInactiveAuthoring || closeRequested || closePromptOpen || closingDocument != null || exitApproved)
            return;
        var registry = ExternalPreviewRegistry;
        if (pending.CheckedVersion == registry.Version) return;
        using var lease = registry.Acquire(entry.AssetType);
        pending.CheckedVersion = registry.Version;
        if (lease == null) return; // Retry on a real registered attachment change.
        if (lease.ProviderType != pending.Provider || lease.AdapterType != pending.Adapter)
        { externalPreviewReattachment = null; return; }
        if (lease.RequiresCompiledContent && projectContent == null)
        { pending.CheckedVersion = -1; return; }
        externalPreviewReattachment = null;
        try
        {
            if (!TryOpenExternalAssetPreview(entry)) return;
            if (externalPreview != null) externalPreview.ViewState = pending.ViewState;
            else if (requestedCatalogExternal)
            {
                externalPreviewReattachmentBuild = pending;
                externalPreviewRestoreState = (entry.Id, pending.Provider, pending.ViewState);
            }
        }
        catch (Exception error) { RecordAssetPreviewBuildFailure(entry.Id, error, AssetThumbnailFailure.Broken); }
    }
}
