using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal sealed partial class ProceduralAssetDocument
{
    private EditorDeferredAssetMutation? deferredExtensionMutation;
    internal EditorDeferredAssetMutation BeginExtensionCommandMutation()
    {
        EndEdit();
        if (deferredExtensionMutation != null || SessionHistory == null || historyScope.AuthoringState is not { IsPublished: true } owner)
            throw new InvalidOperationException("The procedural asset cannot begin another native command.");
        var journal = CreateMutationJournal();
        try
        {
            return deferredExtensionMutation = new(SessionHistory, owner, AuthoringGraph, journal,
                (node, index) =>
                {
                    RefreshAuthoringNodeAccess();
                    return node is IMemberNode member ? CanEditAssetSettingNode(member) && !NumericReadOnly(member)
                        : node is IObjectNode collection && CanEditNativeCollectionItem(collection, index);
                }, () => SessionHistory.AssetGraphs.Flush(this), () => deferredExtensionMutation = null);
        }
        catch { journal.Dispose(); throw; }
    }
}
