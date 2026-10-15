using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    private EditorDeferredAssetMutation? deferredExtensionMutation;
    private bool HasPendingExtensionMutation => deferredExtensionMutation?.HasChanges == true;
    internal bool HasActiveNativeMutationJournal => suppressOwnScalarRecording > 0 || deferredExtensionMutation != null;
    internal EditorDeferredAssetMutation BeginExtensionCommandMutation()
    {
        EndEdits();
        if (deferredExtensionMutation != null || SessionHistory == null || historyScope.AuthoringState is not { IsPublished: true } owner)
            throw new InvalidOperationException("The hierarchy cannot begin another native command.");
        var before = revision; var after = ++nextRevision;
        var journal = new EditorQuantumMutation(HierarchyGraph, () => { revision = before; InvalidateHierarchyAuthoringPaths(); },
            () => { revision = after; InvalidateHierarchyAuthoringPaths(); });
        suppressOwnScalarRecording++;
        try
        {
            return deferredExtensionMutation = new(SessionHistory, owner, HierarchyGraph, journal,
                (node, index) => node is IMemberNode member ? CanEditHierarchyNode(node) && AssetMetadataDocument.CanAssignAuthoringNode(member)
                    : node is IAssetObjectNode collection && CanEditHierarchyItem(collection, index),
                () => SessionHistory.HierarchyGraphs.Flush(this), () => { suppressOwnScalarRecording--; deferredExtensionMutation = null; });
        }
        catch { suppressOwnScalarRecording--; journal.Dispose(); throw; }
    }
}
