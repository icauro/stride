using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool CanClearAssetLink(AssetEntry entry, bool archetype)
    {
        if (workspace == null || sessionHistory == null || authoring == null || projectLoading != null || assetPreviewBuild != null
            || IsAuthoringPipelineUpdating || IsUserProjectBuilding || IsAssetImportUpdating || closeRequested || closePromptOpen || closingDocument != null
            || typeof(SourceCodeAsset).IsAssignableFrom(entry.AssetType)) return false;
        var current = workspace.Assets.FirstOrDefault(candidate => candidate.Id == entry.Id);
        if (current == null || !SamePath(current.Path, entry.Path) || current.AssetType != entry.AssetType) return false;
        var item = workspace.FindAsset(entry.Id);
        if (!IsEditableAssetPackage(item.Package) || !SamePath(item.FullPath.ToOSPath(), entry.Path)
            || File.Exists(entry.Path) && (File.GetAttributes(entry.Path) & FileAttributes.ReadOnly) != 0) return false;
        var retained = authoring.Find(entry.Id)?.Document as IAssetTagDocument;
        var asset = retained?.AuthoredAsset ?? item.Asset;
        return (retained?.CanSave ?? true) && (archetype ? asset.Archetype != null : asset.Replaces != null);
    }

    private void ClearAssetLink(AssetEntry entry, bool archetype)
    {
        if (!CanClearAssetLink(entry, archetype)) return;
        documents.CommitAll();
        var owner = GetAssetTagDocument(entry, false); owner.CommitEdits();
        (AssetPropertyGraph Graph, Action<Action, string> Mutate) context = owner switch
        {
            AssetMetadataDocument metadata => (metadata.AuthoringGraph, metadata.ApplyInheritedAssetMutation),
            EntityHierarchyDocument hierarchy => (hierarchy.HierarchyGraph, hierarchy.ApplyInheritedGraphMutation),
            ProceduralAssetDocument procedural => (procedural.AuthoringGraph, procedural.ApplyInheritedAssetMutation),
            _ => throw new InvalidOperationException("This asset has no editable canonical inheritance graph."),
        };
        var name = archetype ? nameof(Asset.Archetype) : nameof(Asset.Replaces);
        var member = context.Graph.RootNode[name];
        var state = authoring!.Find(entry.Id);
        // These two native root metadata members are NonOverridable. Dedicated
        // WPF link commands assign them directly; ordinary property override
        // permission must not reject this separately certified root operation.
        if (!CanClearAssetLink(entry, archetype) || !owner.CanSave || state is not { IsPublished: true, IsDisposed: false }
            || !ReferenceEquals(state.Document, owner) || !SamePath(state.CanonicalPath, entry.Path)
            || !ReferenceEquals(context.Graph.RootNode.Retrieve(), owner.AuthoredAsset)
            || !ReferenceEquals(member.Parent, context.Graph.RootNode) || member.MemberDescriptor.Name != name
            || !member.MemberDescriptor.HasSet || UnknownAssetData.IsProxyNode(context.Graph.RootNode) || UnknownAssetData.IsProxyNode(member)
            || context.Graph.IsInheritedNodeSealed(context.Graph.RootNode) || context.Graph.IsInheritedNodeSealed(member))
            throw new InvalidOperationException("This asset link is sealed or read-only.");
        var title = archetype ? "Clear archetype" : "Clear replaces";
        // Base graph nodes remain needed by the exact override snapshot throughout undo history.
        IDisposable? historyBase = null;
        EditorAuthoringState? historyBaseState = null;
        AssetPropertyGraph? historyBaseGraph = null;
        var capturedWorkspace = workspace;
        var capturedHistory = sessionHistory;
        if (archetype && owner.AuthoredAsset.Archetype is { } link)
        {
            historyBaseState = authoring!.Find(link.Id) ?? throw new InvalidOperationException("The archetype has no canonical authored owner.");
            historyBaseGraph = historyBaseState.Document switch
            {
                EntityHierarchyDocument hierarchy => hierarchy.HierarchyGraph,
                IEditorAssetGraphDocument source => source.AuthoringGraph,
                _ => throw new InvalidOperationException("The archetype has no canonical native asset graph."),
            };
            historyBase = historyBaseState.AcquireHistory();
        }
        var published = false;
        void RefreshLinks()
        {
            sessionHistory!.HierarchyGraphs.RefreshPublished();
            sessionHistory.AssetGraphs.RefreshPublished();
            sessionHistory.UiLibraryGraphs.RefreshPublished();
        }
        try
        {
            using var capture = sessionHistory!.CaptureOperationsAtomic(() =>
            {
                context.Mutate(() =>
                {
                    member.Update(null);
                    if (archetype) context.Graph.ClearAllOverrides();
                }, title);
                RefreshLinks();
            });
            capture.Publish(sessionHistory, title, operation =>
            {
                var guarded = new GuardedOperation(operation, (_, apply, compensate) =>
                {
                    var previousLink = owner.AuthoredAsset.Archetype;
                    var installedBase = false;
                    if (!ReferenceEquals(workspace, capturedWorkspace) || !ReferenceEquals(sessionHistory, capturedHistory)
                        || !ReferenceEquals(authoring?.Find(entry.Id), state) || !state.IsPublished || state.IsDisposed
                        || !ReferenceEquals(context.Graph.RootNode.Retrieve(), owner.AuthoredAsset))
                        throw new InvalidOperationException("The asset link replay lost its canonical native generation.");
                    if (historyBaseState != null && historyBaseGraph != null)
                    {
                        if (historyBaseState.AssetId is not { } baseId || historyBaseState.IsDisposed || !historyBaseState.IsPublished
                            || !ReferenceEquals(authoring?.Find(baseId), historyBaseState)
                            || historyBaseState.Document is not IAssetTagDocument baseOwner
                            || baseOwner.AuthoredAsset.Id != baseId || historyBaseGraph.Id != baseId
                            || !ReferenceEquals(historyBaseGraph.RootNode.Retrieve(), baseOwner.AuthoredAsset)
                            || !ReferenceEquals(historyBaseGraph.Container.NodeContainer, context.Graph.Container.NodeContainer))
                            throw new InvalidOperationException("The asset link replay lost its exact retained base graph.");
                        var registered = context.Graph.Container.TryGetGraph(baseId);
                        if (registered != null && !ReferenceEquals(registered, historyBaseGraph))
                            throw new InvalidOperationException("The native base graph registration belongs to a different generation.");
                        // Clear removes the live registration. Undo's native journal
                        // restores Archetype and calls RefreshBase before RefreshLinks;
                        // make its exact history-retained graph available first.
                        if (registered == null) { context.Graph.Container.RegisterGraph(historyBaseGraph); installedBase = true; }
                    }
                    var applied = false;
                    try { apply(); applied = true; RefreshLinks(); }
                    catch (Exception failure)
                    {
                        try
                        {
                            if (applied) { compensate(); RefreshLinks(); }
                            // Restore only our temporary registration after native
                            // compensation certified the original link value. Keep
                            // ownership intact when compensation remains unresolved.
                            if (installedBase && historyBaseState?.AssetId is { } baseId)
                            {
                                if (!Equals(owner.AuthoredAsset.Archetype, previousLink))
                                    throw new InvalidOperationException("Asset link compensation did not restore its original archetype.");
                                var registered = context.Graph.Container.TryGetGraph(baseId);
                                if (registered != null && !ReferenceEquals(registered, historyBaseGraph))
                                    throw new InvalidOperationException("Asset link compensation replaced the retained base graph registration.");
                                if (registered != null) context.Graph.Container.UnregisterGraph(baseId);
                            }
                        }
                        catch (Exception recovery) { throw new AggregateException("Asset link replay and restoration failed.", failure, recovery); }
                        throw;
                    }
                });
                return historyBase == null ? guarded : new RetainedOperation(guarded, [historyBase]);
            });
            published = true;
        }
        finally { if (!published) historyBase?.Dispose(); }
    }
}
