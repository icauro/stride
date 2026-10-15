using Stride.Assets.Entities;
using Stride.Core.Assets;
using Stride.Core.Transactions;
using Stride.Core.Assets.Quantum;
using Stride.Core.Diagnostics;
using Stride.Assets.Quantum;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    private EntityHierarchyPropertyGraph? hierarchyGraph;
    private AssetItem? hierarchyItem;
    private readonly LoggerResult hierarchyLog = new();
    internal EntityHierarchyPropertyGraph HierarchyGraph { get { EnsureHierarchyGraph(); return hierarchyGraph!; } }
    internal long StructureVersion { get; private set; }
    internal bool CanCreateEntity(Entity? parent) => CanAuthorHierarchyChildren(parent);

    internal Entity CreateEmptyEntity(Entity? parent = null)
        => InsertNewEntity(new Entity("Entity"), parent);

    internal Entity InsertNewEntity(Entity entity, Entity? parent = null, string folder = "")
    {
        EndEdits();
        if (!CanCreateEntity(parent)) throw new InvalidOperationException("The hierarchy destination is not editable.");
        if (folder.Length > 0 && !GetHierarchyFolders(parent?.Id).Contains(folder, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected hierarchy folder no longer exists.");
        EnsureHierarchyGraph();
        if (entity.Transform.Parent != null || entity.Transform.Children.Count != 0 || Asset.Hierarchy.Parts.ContainsKey(entity.Id))
            throw new InvalidOperationException("A factory must supply a new detached single entity.");
        AssetCollectionItemIdHelper.GenerateMissingItemIds(entity);
        var design = new EntityDesign(entity, folder);
        var collection = new AssetPartCollection<EntityDesign, Entity> { design };
        var index = parent?.Transform.Children.Count ?? Asset.Hierarchy.RootParts.Count;
        if (RequiresHierarchyJournal)
        {
            ApplyInheritedGraphMutation(() =>
            {
                hierarchyGraph!.AddPartToAsset(collection, design, parent, index);
                Selected = entity;
            }, parent == null ? "Create root entity" : $"Create child of {parent.Name}");
            return entity;
        }
        var previousSelection = SnapshotSelection();
        var beforeRevision = revision; var afterRevision = ++nextRevision;
        void Insert()
        {
            hierarchyGraph!.AddPartToAsset(collection, design, parent, index);
            Selected = entity; revision = afterRevision; StructureVersion++;
        }
        void Remove()
        {
            hierarchyGraph!.RemovePartFromAsset(design);
            RestoreSelection(previousSelection.Ids, previousSelection.PrimaryId); revision = beforeRevision; StructureVersion++;
        }
        try
        {
            using (CreateDocumentTransaction())
            {
                Insert();
                RecordOperation(new HierarchyCreationOperation(Remove, Insert));
            }
        }
        catch
        {
            if (Asset.Hierarchy.Parts.ContainsKey(entity.Id)) Remove();
            throw;
        }
        LastAction = parent == null ? "Create root entity" : $"Create child of {parent.Name}";
        return entity;
    }

    private void EnsureHierarchyGraph()
    {
        if (hierarchyGraph != null) return;
        AssetQuantumRegistry.RegisterAssembly(typeof(AssetPropertyGraph).Assembly);
        AssetQuantumRegistry.RegisterAssembly(typeof(EntityHierarchyPropertyGraph).Assembly);
        hierarchyItem = SessionHistory?.HierarchyGraphs.ResolveAssetItem?.Invoke(Asset.Id)?.Clone(keepPackage: true, newAsset: Asset)
            ?? new AssetItem(System.IO.Path.GetFileNameWithoutExtension(Path), Asset);
        hierarchyItem.YamlMetadata.ReplaceWith(loaded.YamlMetadata);
        AssetCollectionItemIdHelper.GenerateMissingItemIds(Asset);
        var container = new AssetPropertyGraphContainer(SessionHistory?.HierarchyGraphs.Nodes ?? new AssetNodeContainer { NodeBuilder = { NodeFactory = new AssetNodeFactory() } });
        hierarchyGraph = (EntityHierarchyPropertyGraph)container.InitializeAsset(hierarchyItem, hierarchyLog)!;
        try
        {
            if (SessionHistory != null) SessionHistory.HierarchyGraphs.Register(this, hierarchyGraph);
            else if (!HasInheritedHierarchy && Asset.Archetype == null) hierarchyGraph.Initialize();
        }
        catch
        {
            if (SessionHistory?.HierarchyGraphs.IsRegistered(this) != true) { hierarchyGraph.Dispose(); hierarchyGraph = null; }
            throw;
        }
    }

    private void PrepareHierarchyMetadata()
    {
        if (hierarchyGraph == null) return;
        hierarchyGraph.PrepareForSave(hierarchyLog, hierarchyItem!);
        if (hierarchyLog.HasErrors) throw new InvalidOperationException("Scene hierarchy serialization metadata could not be prepared.");
    }
    internal void UpdateHierarchyAssetItemIdentity(AssetItem item)
    {
        if (hierarchyGraph == null) return;
        hierarchyGraph.UpdateAssetItemIdentity(item); hierarchyItem = hierarchyGraph.AuthoredItem;
    }

    public void Dispose()
    {
        ReleaseCommonMutationCleanup();
        ReleaseComponentMutationCleanup();
        ReleaseSceneOffsetMutationCleanup();
        historyScope.EnsureMayDispose();
        SessionHistory?.HierarchyGraphs.Unregister(this);
        DisposePropertyPresenters();
        hierarchyGraph?.Dispose();
    }

    private sealed class HierarchyCreationOperation(Action undo, Action redo) : Operation
    {
        protected override void Rollback() => undo();
        protected override void Rollforward() => redo();
    }
}
