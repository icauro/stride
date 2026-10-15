using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private EditorDeferredAssetMutation? deferredExtensionMutation;
    private bool HasPendingExtensionMutation => deferredExtensionMutation?.HasChanges == true;
    internal bool HasActiveNativeMutationJournal => capturingAssetMutation || deferredExtensionMutation != null;
    internal EditorDeferredAssetMutation BeginExtensionCommandMutation()
    {
        CommitEdits();
        if (deferredExtensionMutation != null || SessionHistory == null || historyScope.AuthoringState is not { IsPublished: true } owner)
            throw new InvalidOperationException("The asset cannot begin another native command.");
        var before = revision; var after = ++nextRevision;
        var textureBefore = textureSettingsRevision; var materialBefore = materialSettingsRevision; var skyboxBefore = skyboxSettingsRevision;
        var journal = new EditorQuantumMutation(AuthoringGraph, () =>
        {
            revision = before; textureSettingsRevision = textureBefore; materialSettingsRevision = materialBefore; skyboxSettingsRevision = skyboxBefore;
            RefreshAuthoringFields();
        }, () =>
        {
            revision = after;
            if (TextureSettings != null) textureSettingsRevision = after;
            if (MaterialSettings != null) materialSettingsRevision = after;
            if (SkyboxSettings != null) skyboxSettingsRevision = after;
            RefreshAuthoringFields();
        });
        IEditorNativeMutationObserver? mutationObserver = null;
        try
        {
            mutationObserver = BeginNativeAuthoringMutation();
            capturingAssetMutation = true;
            return deferredExtensionMutation = new(SessionHistory, owner, AuthoringGraph, journal, (node, index) =>
            {
                RefreshAuthoringNodeAccess();
                return node is IMemberNode member ? CanEditAssetSettingNode(node) && CanAssignAuthoringNode(member) && !IsExtensionPropertyReadOnly(member)
                    : node is IAssetObjectNode collection && CanEditAssetSettingItem(collection, index);
            }, () => { mutationObserver?.Complete(); SessionHistory.AssetGraphs.Flush(this); SessionHistory.UiLibraryGraphs.Flush(this); },
                () => { mutationObserver?.Dispose(); capturingAssetMutation = false; pendingSourceOverrides = null; deferredExtensionMutation = null; });
        }
        catch { mutationObserver?.Dispose(); capturingAssetMutation = false; journal.Dispose(); throw; }
    }
}
