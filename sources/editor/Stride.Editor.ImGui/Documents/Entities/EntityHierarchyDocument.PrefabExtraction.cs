using Stride.Assets.Entities;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets.Quantum.Visitors;
using Stride.Core.Assets.Yaml;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Core.Yaml;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal bool CanExtractPrefab => this is SceneDocument && CanSave && SelectedCount > 0 && Asset.Archetype == null;
    internal bool IsCapturingPrefabExtraction { get; private set; }

    internal PreparedPrefabExtraction PreparePrefabExtraction()
    {
        if (!CanExtractPrefab) throw new InvalidOperationException("A prefab cannot be created from this selection.");
        EndEdits(); EnsureHierarchyGraph();
        var roots = GetSelectedTransformRoots();
        var originals = roots.SelectMany(Subtree).DistinctBy(entity => entity.Id).ToArray();
        var externalReferences = roots.SelectMany(root => ExternalReferenceCollector.GetExternalReferenceAccessors(
            hierarchyGraph!.Definition, hierarchyGraph.Container.NodeContainer.GetOrCreateNode(root)))
            .SelectMany(pair => pair.Value.Select(accessor => new ExtractionExternalReference(accessor, pair.Key,
                accessor.Node is IAssetObjectNode collection ? collection.IndexToId(accessor.Index) : ItemId.Empty))).ToArray();
        var overrides = originals.ToDictionary(entity => entity.Id,
            entity => AssetPropertyGraph.GenerateOverridesForSerialization(hierarchyGraph!.Container.NodeContainer.GetOrCreateNode(entity)));
        var cloned = AssetCompositeHierarchyPropertyGraph<EntityDesign, Entity>.CloneSubHierarchies(
            hierarchyGraph!.Container.NodeContainer, Asset, roots.Select(entity => entity.Id),
            SubHierarchyCloneFlags.CleanExternalReferences | SubHierarchyCloneFlags.GenerateNewIdsForIdentifiableObjects,
            out Dictionary<Guid, Guid> identities);
        AssetPartsAnalysis.GenerateNewBaseInstanceIds(cloned);
        foreach (var mapping in identities)
            if (cloned.Parts.TryGetValue(mapping.Value, out var part))
                AssetPropertyGraph.ApplyOverrides((IAssetNode)hierarchyGraph.Container.NodeContainer.GetOrCreateNode(part.Entity), overrides[mapping.Key]);
        var prefab = new PrefabAsset { Hierarchy = cloned };
        AssetCollectionItemIdHelper.GenerateMissingItemIds(prefab);
        var metadata = new AttachedYamlAssetMetadata();
        var prefabNode = hierarchyGraph.Container.NodeContainer.GetOrCreateNode(prefab);
        metadata.AttachMetadata(AssetObjectSerializerBackend.OverrideDictionaryKey,
            AssetPropertyGraph.GenerateOverridesForSerialization(prefabNode));
        metadata.AttachMetadata(AssetObjectSerializerBackend.ObjectReferencesKey,
            hierarchyGraph.GenerateObjectReferencesForSerialization(prefabNode));
        return new PreparedPrefabExtraction(this, prefab, metadata, identities, overrides,
            externalReferences.Where(reference => !identities.ContainsKey(reference.Value.Id)).DistinctBy(reference => reference.Accessor).ToArray(),
            cloned.RootParts.FirstOrDefault()?.Name ?? "Prefab", revision);
    }

    internal static PreparedPrefabExtraction[] PreparePrefabExtractions(IReadOnlyList<EntityHierarchyDocument> owners)
    {
        if (owners.Count == 0 || owners.Distinct().Count() != owners.Count)
            throw new InvalidOperationException("Prefab extraction needs distinct source owners.");
        var session = owners[0].SessionHistory ?? throw new InvalidOperationException("Prefab extraction requires the canonical session.");
        foreach (var owner in owners)
            if (!ReferenceEquals(owner.SessionHistory, session) || !ReferenceEquals(session.Registry.Find(owner.Asset.Id)?.Document, owner)
                || !owner.CanExtractPrefab)
                throw new InvalidOperationException("The selected hierarchy owner changed.");
        var prepared = owners.Select(owner => owner.PreparePrefabExtraction()).ToArray();
        var graph = owners[0].HierarchyGraph;
        if (owners.Any(owner => !ReferenceEquals(owner.HierarchyGraph.Container.NodeContainer, graph.Container.NodeContainer)))
            throw new InvalidOperationException("Prefab sources require the real shared node container.");
        var prefab = new PrefabAsset();
        foreach (var extraction in prepared) prefab.Hierarchy.MergeInto(extraction.Prefab.Hierarchy);
        AssetCollectionItemIdHelper.GenerateMissingItemIds(prefab);
        var metadata = new AttachedYamlAssetMetadata();
        var node = graph.Container.NodeContainer.GetOrCreateNode(prefab);
        metadata.AttachMetadata(AssetObjectSerializerBackend.OverrideDictionaryKey, AssetPropertyGraph.GenerateOverridesForSerialization(node));
        metadata.AttachMetadata(AssetObjectSerializerBackend.ObjectReferencesKey, graph.GenerateObjectReferencesForSerialization(node));
        foreach (var extraction in prepared) extraction.BindMergedPrefab(prefab, metadata);
        return prepared;
    }

    internal sealed class PreparedPrefabExtraction
    {
        internal EntityHierarchyDocument Source { get; }
        internal PrefabAsset Prefab { get; private set; }
        internal AttachedYamlAssetMetadata Metadata { get; private set; }
        internal string SuggestedName { get; }
        private readonly Dictionary<Guid, Guid> identities;
        private readonly Dictionary<Guid, YamlAssetMetadata<OverrideType>> beforeOverrides;
        private readonly ExtractionExternalReference[] externalReferences;
        private readonly long beforeRevision;
        private readonly Guid instanceId = Guid.NewGuid();

        internal PreparedPrefabExtraction(EntityHierarchyDocument source, PrefabAsset prefab, AttachedYamlAssetMetadata metadata,
            Dictionary<Guid, Guid> identities, Dictionary<Guid, YamlAssetMetadata<OverrideType>> overrides,
            ExtractionExternalReference[] externalReferences, string suggestedName, long beforeRevision)
        {
            Source = source; Prefab = prefab; Metadata = metadata; this.identities = identities;
            beforeOverrides = overrides; SuggestedName = suggestedName;
            this.externalReferences = externalReferences; this.beforeRevision = beforeRevision;
        }
        internal void BindMergedPrefab(PrefabAsset prefab, AttachedYamlAssetMetadata metadata)
        { Prefab = prefab; Metadata = metadata; }

        internal void RelinkWithJournal(AssetItem item)
        {
            var graph = Source.HierarchyGraph;
            if (Source.revision != beforeRevision || !ReferenceEquals(item.Asset, Prefab)
                || beforeOverrides.Keys.Any(id => !Source.Asset.Hierarchy.Parts.ContainsKey(id)))
                throw new InvalidOperationException("The prefab selection changed before creation.");
            if (graph.Container.TryGetGraph(Prefab.Id) == null)
                throw new InvalidOperationException("The prefab must have its canonical shared graph before linking entities.");
            Source.ApplyInheritedGraphMutation(() =>
            {
                Source.IsCapturingPrefabExtraction = true;
                try
                {
                    foreach (var mapping in identities)
                        if (Source.Asset.Hierarchy.Parts.TryGetValue(mapping.Key, out var part))
                            graph.Container.NodeContainer.GetOrCreateNode(part)[nameof(EntityDesign.Base)]
                                .Update(new BasePart(item.ToReference(), mapping.Value, instanceId));
                    graph.RefreshBase();
                    foreach (var id in beforeOverrides.Keys)
                        ((IAssetNode)graph.Container.NodeContainer.GetOrCreateNode(Source.Asset.Hierarchy.Parts[id].Entity)).ResetOverrideRecursively();
                    // References omitted from the new prefab remain real local scene overrides.
                    var reachable = new HashSet<IGraphNode>();
                    var visitor = new AssetGraphVisitorBase(graph.Definition);
                    visitor.Visiting += (node, _) => reachable.Add(node);
                    foreach (var id in beforeOverrides.Keys)
                        visitor.Visit(graph.Container.NodeContainer.GetOrCreateNode(Source.Asset.Hierarchy.Parts[id].Entity));
                    foreach (var reference in externalReferences)
                    {
                        if (!reachable.Contains(reference.Accessor.Node))
                            throw new InvalidOperationException("The selected prefab topology cannot preserve an external scene reference.");
                        if (reference.Accessor.Node is IAssetMemberNode member)
                        { member.Update(reference.Value); member.OverrideContent(true); }
                        else if (reference.Accessor.Node is IAssetObjectNode collection)
                        {
                            var index = reference.Item == ItemId.Empty ? reference.Accessor.Index : collection.IdToIndex(reference.Item);
                            collection.Update(reference.Value, index); collection.OverrideItem(true, index);
                        }
                    }
                }
                finally { Source.IsCapturingPrefabExtraction = false; }
            }, "Link entities to extracted prefab");
            Source.SessionHistory!.HierarchyGraphs.Refresh(Source);
        }
    }
    internal sealed record ExtractionExternalReference(NodeAccessor Accessor, IIdentifiable Value, ItemId Item);
}
