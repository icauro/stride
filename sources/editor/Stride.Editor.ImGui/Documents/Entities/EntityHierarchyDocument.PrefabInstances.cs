using Stride.Assets.Entities;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Mathematics;
using Stride.Core.Quantum;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal bool CanInsertPrefab(Guid? parentId, string folder, int index)
    {
        if (!CanSave || Asset.Archetype != null) return false;
        Entity? parent = null;
        if (parentId is { } id)
        {
            if (!Asset.Hierarchy.Parts.TryGetValue(id, out var design)) return false;
            parent = design.Entity;
        }
        if (!CanCreateEntity(parent) || folder.Length > 0 && !GetHierarchyFolders(parentId).Contains(folder, StringComparer.OrdinalIgnoreCase)) return false;
        var count = parent?.Transform.Children.Count ?? Asset.Hierarchy.RootParts.Count;
        return index >= -1 && index <= count;
    }

    internal PrefabInstanceCreation InsertPrefab(PrefabDocument source, string sourceUrl, Guid? parentId,
        string folder, int index, bool createContainer, Vector3? scenePosition)
    {
        if (!CanInsertPrefab(parentId, folder, index) || source.HasUnloadableObjects || source.Asset.Hierarchy.RootParts.Count == 0
            || SessionHistory == null || !ReferenceEquals(source.SessionHistory, SessionHistory))
            throw new InvalidOperationException("The prefab cannot be added to this scene.");
        var targetState = historyScope.AuthoringState;
        var sourceDocument = (EntityHierarchyDocument)source;
        var sourceState = sourceDocument.historyScope.AuthoringState;
        if (targetState is not { IsPublished: true } || sourceState is not { IsPublished: true }
            || !ReferenceEquals(SessionHistory.Registry.Find(Asset.Id), targetState)
            || !ReferenceEquals(SessionHistory.Registry.Find(source.Asset.Id), sourceState))
            throw new InvalidOperationException("Prefab insertion requires the current canonical authored graphs.");
        using var targetLease = targetState.AcquireView();
        using var sourceLease = sourceState.AcquireView();
        EndEdits(); source.EndEdits(); EnsureHierarchyGraph(); sourceDocument.EnsureHierarchyGraph();
        var parent = parentId is { } owner ? Asset.Hierarchy.Parts[owner].Entity : null;
        if (index == -1) index = parent?.Transform.Children.Count ?? Asset.Hierarchy.RootParts.Count;

        // This is precisely the derived-asset pipeline used by CreatePrefabInstance,
        // exposing its real remapping for editor selection/reference integration.
        var derived = (PrefabAsset)source.Asset.CreateDerivedAsset(sourceUrl, out var remapping);
        var hierarchy = derived.Hierarchy;
        var instanceId = hierarchy.Parts.Values.First().Base?.InstanceId
            ?? throw new InvalidOperationException("The derived prefab has no instance base.");
        if (hierarchy.Parts.Values.Any(part => part.Base?.BasePartAsset.Id != source.Asset.Id || part.Base.InstanceId != instanceId)
            || hierarchy.Parts.Keys.Any(id => Asset.Hierarchy.Parts.ContainsKey(id)))
            throw new InvalidOperationException("The derived prefab has invalid or conflicting identities.");
        if (createContainer)
        {
            var container = new Entity(System.IO.Path.GetFileNameWithoutExtension(source.Path));
            foreach (var root in hierarchy.RootParts.ToArray()) container.Transform.Children.Add(root.Transform);
            hierarchy.RootParts.Clear(); hierarchy.RootParts.Add(container);
            hierarchy.Parts.Add(new EntityDesign(container, folder));
        }
        else
        {
            foreach (var root in hierarchy.RootParts)
            {
                var design = hierarchy.Parts[root.Id];
                design.Folder = string.IsNullOrEmpty(design.Folder) ? folder : folder.Length == 0 ? design.Folder : folder + "/" + design.Folder;
            }
        }
        var roots = hierarchy.RootParts.ToArray();
        var placement = Vector3.Zero;
        if (scenePosition is { } point)
        {
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)) throw new ArgumentException("The prefab position must be finite.");
            placement = point;
            if (parent != null)
            {
                parent.Transform.UpdateWorldMatrix();
                var determinant = parent.Transform.WorldMatrix.Determinant();
                if (!float.IsFinite(determinant) || Math.Abs(determinant) < 1e-12f) throw new InvalidOperationException("The parent world transform is singular.");
                placement = Vector3.TransformCoordinate(point, Matrix.Invert(parent.Transform.WorldMatrix));
            }
        }
        foreach (var root in roots) root.Transform.Position += placement;
        AssetCollectionItemIdHelper.GenerateMissingItemIds(hierarchy);
        var beforeSelection = SnapshotSelection(); var beforeRevision = revision; var afterRevision = ++nextRevision;
        void Remove()
        {
            foreach (var root in roots.Reverse())
            {
                if (!Asset.Hierarchy.Parts.TryGetValue(root.Id, out var design)) continue;
                if (Asset.Hierarchy.RootParts.Contains(root) || root.Transform.Parent?.Entity == parent && parent != null)
                    hierarchyGraph!.RemovePartFromAsset(design);
                else
                {
                    var partsNode = hierarchyGraph!.Container.NodeContainer.GetOrCreateNode(Asset.Hierarchy.Parts);
                    foreach (var entity in Subtree(root).Reverse())
                        if (Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var part)) partsNode.Remove(part, new NodeIndex(entity.Id));
                }
            }
            HasInheritedHierarchy = Asset.Hierarchy.Parts.Values.Any(part => part.Base != null);
            SessionHistory.HierarchyGraphs.Refresh(this);
            RestoreSelection(beforeSelection.Ids, beforeSelection.PrimaryId); revision = beforeRevision; StructureVersion++;
        }
        void Insert()
        {
            try
            {
                for (var rootIndex = 0; rootIndex < roots.Length; rootIndex++)
                {
                    var root = roots[rootIndex];
                    hierarchyGraph!.AddPartToAsset(hierarchy.Parts, hierarchy.Parts[root.Id], parent, index + rootIndex);
                    if (hierarchy.Parts[root.Id].Base != null && placement != Vector3.Zero)
                        ((IAssetMemberNode)hierarchyGraph.Container.NodeContainer.GetOrCreateNode(root.Transform)[nameof(TransformComponent.Position)]).OverrideContent(true);
                }
                HasInheritedHierarchy = true;
                SessionHistory.HierarchyGraphs.Refresh(this);
                RestoreSelection(roots.Select(root => root.Id), roots[^1].Id); revision = afterRevision; StructureVersion++;
            }
            catch (Exception failure)
            {
                try { Remove(); }
                catch (Exception recovery) { throw new AggregateException("Prefab insertion and restoration failed.", failure, recovery); }
                throw;
            }
        }
        var applied = false; var recorded = false;
        try
        {
            using var transaction = CreateDocumentTransaction("Add prefab: " + System.IO.Path.GetFileNameWithoutExtension(source.Path));
            Insert(); applied = true;
            RecordOperation(new HierarchyCreationOperation(Remove, Insert), sourceState); recorded = true;
        }
        catch { if (applied && !recorded) Remove(); throw; }
        LastAction = "Add prefab: " + System.IO.Path.GetFileNameWithoutExtension(source.Path);
        return new(instanceId, roots, remapping);
    }
}
