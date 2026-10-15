using Stride.UI.Assets;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Diagnostics;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private AssetPropertyGraph? authoringGraph;
    private AssetItem? authoringItem;
    private AssetGraphOverrideSnapshot? pendingSourceOverrides;
    private bool capturingAssetMutation;
    private bool uiGraphInitializationPending;
    private bool initializingUiGraph;
    private readonly LoggerResult authoringGraphLog = new();
    private readonly HashSet<IAssetNode> authoringNodes = [];
    private readonly HashSet<IAssetNode> inheritedSealedNodes = [];
    private readonly HashSet<IAssetNode> unknownAuthoringNodes = [];
    internal AssetPropertyGraph AuthoringGraph
    {
        get
        {
            if (authoringGraph != null)
            {
                if (uiGraphInitializationPending && !initializingUiGraph) CompleteUiGraphInitialization();
                return authoringGraph;
            }
            AssetQuantumRegistry.RegisterAssembly(typeof(AssetPropertyGraph).Assembly);
            AssetQuantumRegistry.RegisterAssembly(AuthoredAsset.GetType().Assembly);
            authoringItem = SessionHistory?.HierarchyGraphs.ResolveAssetItem?.Invoke(AuthoredAsset.Id)?.Clone(keepPackage: true, newAsset: AuthoredAsset)
                ?? new AssetItem(System.IO.Path.GetFileNameWithoutExtension(Path), AuthoredAsset);
            authoringItem.YamlMetadata.ReplaceWith(loaded.YamlMetadata); DocumentAssetPath.Update(authoringItem, Path);
            var container = new AssetPropertyGraphContainer(SessionHistory?.HierarchyGraphs.Nodes
                ?? new AssetNodeContainer { NodeBuilder = { NodeFactory = new AssetNodeFactory() } });
            authoringGraph = container.InitializeAsset(authoringItem, authoringGraphLog)
                ?? throw new InvalidOperationException("This asset has no authored Quantum graph.");
            try
            {
                SessionHistory?.AssetGraphs.Register(this, authoringGraph);
                if (this is UIDocument)
                {
                    uiGraphInitializationPending = true;
                    CompleteUiGraphInitialization();
                    return authoringGraph;
                }
                authoringGraph.Changing += OnAuthoringMemberChanging; authoringGraph.ItemChanging += OnAuthoringItemChanging;
                RefreshAuthoringNodeAccess();
                return authoringGraph;
            }
            catch
            {
                if (SessionHistory?.AssetGraphs.IsRegistered(this) != true) { authoringGraph.Dispose(); authoringGraph = null; }
                throw;
            }
        }
    }
    private void CompleteUiGraphInitialization()
    {
        initializingUiGraph = true;
        try
        {
            if (this is UIDocument ui && authoringGraph is Stride.UI.Assets.UIAssetPropertyGraph graph) SessionHistory?.UiLibraryGraphs.Register(ui, graph);
            authoringGraph!.Changing -= OnAuthoringMemberChanging; authoringGraph.ItemChanging -= OnAuthoringItemChanging;
            authoringGraph.Changing += OnAuthoringMemberChanging; authoringGraph.ItemChanging += OnAuthoringItemChanging;
            RefreshAuthoringNodeAccess(); uiGraphInitializationPending = false;
        }
        finally { initializingUiGraph = false; }
    }
    private AssetPropertyGraph EnsureSettingsAuthoringGraph(out AssetItem item)
    {
        var graph = AuthoringGraph; item = graph.AuthoredItem; return graph;
    }
    private void OnAuthoringMemberChanging(object? _, MemberNodeChangeEventArgs e) => CaptureSourceOverrides();
    private void OnAuthoringItemChanging(object? _, ItemChangeEventArgs e) => CaptureSourceOverrides();
    private void CaptureSourceOverrides()
    {
        if (pendingSourceOverrides == null && !capturingAssetMutation && authoringGraph?.UpdatingPropertyFromBase != true
            && SessionHistory?.Stack.RollInProgress != true && SessionHistory?.IsCompensating != true)
            pendingSourceOverrides = new(AuthoringGraph, AuthoringGraph.RootNode);
    }
    internal void UpdateAuthoringAssetIdentity(AssetItem item)
    {
        if (authoringGraph == null) return;
        authoringGraph.UpdateAssetItemIdentity(item); authoringItem = authoringGraph.AuthoredItem;
        if (textureSettingsItem != null) textureSettingsItem = authoringItem;
        if (materialSettingsItem != null) materialSettingsItem = authoringItem;
        if (gameSettingsItem != null) gameSettingsItem = authoringItem;
        if (spriteSettingsItem != null) spriteSettingsItem = authoringItem;
        if (skyboxSettingsItem != null) skyboxSettingsItem = authoringItem;
    }
    private Operation PreserveAuthoringOverrides(Operation operation)
    {
        if (pendingSourceOverrides == null || authoringGraph == null) return operation;
        var before = pendingSourceOverrides; pendingSourceOverrides = null;
        RefreshAuthoringNodeAccess();
        var after = new AssetGraphOverrideSnapshot(authoringGraph, authoringGraph.RootNode);
        return new GuardedOperation(operation, (forward, replay) =>
        {
            var currentRevision = revision; var currentTexture = textureSettingsRevision;
            var currentMaterial = materialSettingsRevision; var currentSkybox = skyboxSettingsRevision;
            using var recovery = new EditorQuantumMutation(authoringGraph, () =>
            {
                revision = currentRevision; textureSettingsRevision = currentTexture; materialSettingsRevision = currentMaterial; skyboxSettingsRevision = currentSkybox;
                RefreshAuthoringFields();
            }, () => { });
            try { replay(); (forward ? after : before).Restore(); authoringGraph.RefreshBase(); RefreshAuthoringFields(); }
            catch (Exception failure)
            {
                try { recovery.RestoreInitial(); }
                catch (Exception restore) { throw new AggregateException("Asset operation replay and restoration failed.", failure, restore); }
                throw;
            }
        });
    }
    internal void ApplyInheritedAssetMutation(Action mutate, string title)
        => ApplyInheritedAssetMutation(mutate, title, out _);
    private Guid ApplyPreparedReferenceMutation(Action mutate, string title)
    {
        ApplyInheritedAssetMutation(mutate, title, out var id);
        return id;
    }
    private void ApplyInheritedAssetMutation(Action mutate, string title, out Guid operationId)
    {
        CommitEdits();
        var graph = AuthoringGraph;
        using var mutationObserver = BeginNativeAuthoringMutation();
        using var unknownGuard = new EditorUnknownMutationGuard(graph);
        var beforeRevision = revision; var beforeTexture = textureSettingsRevision;
        var beforeMaterial = materialSettingsRevision; var beforeSkybox = skyboxSettingsRevision;
        var afterRevision = ++nextRevision;
        using var journal = new EditorQuantumMutation(graph, () =>
        {
            revision = beforeRevision; textureSettingsRevision = beforeTexture; materialSettingsRevision = beforeMaterial; skyboxSettingsRevision = beforeSkybox;
            RefreshAuthoringFields();
        }, () =>
        {
            revision = afterRevision;
            if (TextureSettings != null) textureSettingsRevision = afterRevision;
            if (MaterialSettings != null) materialSettingsRevision = afterRevision;
            if (SkyboxSettings != null) skyboxSettingsRevision = afterRevision;
            RefreshAuthoringFields();
        });
        operationId = journal.Id;
        EditorOperationCapture? propagated = null; var published = false;
        capturingAssetMutation = true;
        try
        {
            using var transaction = CreateDocumentTransaction(title);
            void Apply()
            {
                mutate();
                mutationObserver?.Complete();
            }
            if (SessionHistory!.IsCapturingOperations) Apply(); else propagated = SessionHistory.CaptureOperationsAtomic(Apply);
            journal.Complete();
            try
            {
                var owner = historyScope.AuthoringState ?? throw new InvalidOperationException("The asset has no authored owner.");
                if (propagated == null) SessionHistory.PushOperation(journal, owner);
                else SessionHistory.PushOperation(new CompositeOperation(propagated.Entries.Select(entry => entry.Operation).Append(journal)),
                    propagated.Participants.Append(owner).Distinct().ToArray());
                published = true;
            }
            catch { if (propagated != null) SessionHistory.RestoreFailedCapture(propagated); throw; }
        }
        catch (Exception failure)
        {
            if (!published)
                try { SessionHistory!.CompensateInitialMutation(journal.RestoreInitial); }
                catch (Exception recovery)
                {
                    SessionHistory!.RetainFailedInitialOperation(journal, historyScope.AuthoringState!);
                    throw new AggregateException("Inherited asset changes and restoration failed.", failure, recovery);
                }
            throw;
        }
        finally { capturingAssetMutation = false; propagated?.Dispose(); }
    }
    private void RefreshAuthoringFields()
    {
        RefreshAuthoringNodeAccess();
        if (textureSettingsGraph != null) { RefreshTextureSettingsFields(); textureSettingsChangeVersion++; }
        if (materialSettingsGraph != null) { RefreshMaterialSettingsFields(); materialSettingsChangeVersion++; }
        if (gameSettingsGraph != null) RefreshGameSettingsFields();
        if (spriteSettingsGraph != null) { RefreshSpriteSettingsFields(); SpriteSettingsEditVersion++; }
        if (skyboxSettingsGraph != null) SkyboxSettingsEditVersion++;
        if (fontSettingsGraph != null) { RefreshFontSettingsFields(); FontSettingsEditVersion++; }
        if (importSettingsGraph != null) RefreshImportSettingsFields();
    }
    internal static bool CanAssignAuthoringNode(IMemberNode node)
        => !UnknownAssetData.IsProxyNode(node) && node.MemberDescriptor.HasSet && node is IAssetMemberNode { CanOverride: true } member
        && (member.BaseNode is not IAssetMemberNode source || !source.GetContentOverride().HasFlag(OverrideType.Sealed));
    internal bool CanEditAssetSettingNode(IGraphNode node)
        => CanSave && node is IAssetNode asset && authoringNodes.Contains(asset) && !inheritedSealedNodes.Contains(asset) && !unknownAuthoringNodes.Contains(asset)
        && (SessionHistory == null || historyScope.AuthoringState is { } state
            && (ReferenceEquals(SessionHistory.Registry.Find(AuthoredAsset.Id), state) || SessionHistory.IsCapturingOperations && !state.IsPublished));
    private void RefreshAuthoringNodeAccess()
    {
        authoringNodes.Clear(); inheritedSealedNodes.Clear(); unknownAuthoringNodes.Clear();
        if (authoringGraph == null) return;
        var visitor = authoringGraph.CreateReconcilerVisitor();
        visitor.Visiting += (node, path) =>
        {
            if (node is not IAssetNode asset) return;
            authoringNodes.Add(asset);
            if (UnknownAssetData.IsProxyNode(node)) unknownAuthoringNodes.Add(asset);
            for (var current = path; current != null; current = current.GetParent())
            {
                var accessor = current.GetAccessor();
                if (UnknownAssetData.IsProxyNode(accessor.Node)) unknownAuthoringNodes.Add(asset);
                if (accessor.Node is IAssetNode parent && authoringGraph.IsInheritedNodeSealed(parent, accessor.Index))
                { inheritedSealedNodes.Add(asset); break; }
            }
        };
        visitor.Visit(authoringGraph.RootNode);
    }
    internal bool CanResetAssetSettingNode(IGraphNode node)
        => CanEditAssetSettingNode(node) && node is IAssetNode { BaseNode: not null };
    internal bool CanEditAssetSettingItem(IAssetObjectNode collection, NodeIndex index)
        => CanEditAssetSettingNode(collection) && !AuthoringGraph.IsInheritedNodeSealed(collection, index)
        && !IsExtensionItemReadOnly(collection, index);
    internal bool CanResetAssetSettingItem(IAssetObjectNode collection, NodeIndex index)
        => CanEditAssetSettingItem(collection, index) && collection.BaseNode is IAssetObjectNode source
        && collection.TryIndexToId(index, out var id) && id != ItemId.Empty && source.HasId(id);
    internal void ResetAssetSettingItem(IAssetObjectNode collection, NodeIndex index)
    {
        CommitEdits();
        if (!CanResetAssetSettingItem(collection, index)) throw new InvalidOperationException("This item has no editable base value.");
        ApplyInheritedAssetMutation(() => collection.ResetOverrideRecursively(index), "Reset to base value");
    }
    internal void ResetAssetSettingNode(IGraphNode node)
    {
        CommitEdits();
        if (!CanResetAssetSettingNode(node)) throw new InvalidOperationException("This setting has no editable base value.");
        ApplyInheritedAssetMutation(((IAssetNode)node).ResetOverrideRecursively, "Reset to base value");
    }
    internal void RefreshInheritedAssetFields() => RefreshAuthoringFields();
    private bool CanEditArchetypeAsset => CanSave && (AuthoredAsset.Archetype == null || SessionHistory != null);
    private void DisposeAuthoringGraph()
    {
        DisposeExtensionAssetPresentation();
        if (this is UIDocument ui) SessionHistory?.UiLibraryGraphs.Unregister(ui);
        SessionHistory?.AssetGraphs.Unregister(this);
        if (authoringGraph == null) return;
        authoringGraph.Changing -= OnAuthoringMemberChanging; authoringGraph.ItemChanging -= OnAuthoringItemChanging;
        authoringGraph.Dispose(); authoringGraph = null; pendingSourceOverrides = null;
    }
    internal void PrepareAssetGraphMetadata()
    {
        if (authoringGraph == null) return;
        authoringGraph.PrepareForSave(authoringGraphLog, authoringItem!);
        if (authoringGraphLog.HasErrors) throw new InvalidOperationException("Asset graph serialization metadata could not be prepared.");
    }
}
