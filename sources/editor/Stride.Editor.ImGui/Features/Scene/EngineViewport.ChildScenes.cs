using Stride.Assets.Entities;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum.Visitors;
using Stride.Core.Mathematics;
using Stride.Core.Reflection;
using Stride.Core.Serialization;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal sealed partial class EngineViewport
{
    internal Func<AssetId, SceneDocument?>? ResolveChildSceneDocument { get; set; }
    internal Func<EntityHierarchyDocument, Vector3>? ResolveSceneWorldOffset { get; set; }
    internal Func<SceneDocument, bool>? SceneLoaded { get; set; }
    internal Func<SceneDocument, bool>? SceneLocked { get; set; }
    internal Func<EntityHierarchyDocument, Guid, bool>? EntityLocked { get; set; }
    internal Func<long>? ScenePresentationVersion { get; set; }
    private sealed record ChildSceneRuntime(SceneDocument Document, IDisposable Lease, Scene Scene,
        Dictionary<Guid, Entity> Entities, SceneSynchronizationState State, long StructureVersion, Dictionary<Guid, long> ComponentVersions,
        Dictionary<Guid, EntityComponent[]>? ComponentPlan = null);
    private readonly Dictionary<AssetId, ChildSceneRuntime> childScenes = [];
    private readonly Dictionary<AssetId, (SceneDocument Document, AssetId Parent)> desiredChildScenes = [];
    private readonly Dictionary<AssetId, ChildSceneRuntime> preparedChildScenes = [];
    private readonly HashSet<AssetId> visitingChildScenes = [];
    private readonly List<AssetId> removedChildSceneIds = [];
    private readonly Dictionary<AssetId, (SceneDocument Document, IDisposable Lease)> childSceneProbes = [];
    private readonly Dictionary<Entity, SceneDocument> childPickingOwners = new(ReferenceEqualityComparer.Instance);
    internal SceneDocument? PickedChildScene(Entity entity) => childPickingOwners.GetValueOrDefault(entity);
    internal bool CanPickSceneEntity(Entity entity)
    {
        return CanSelectSceneEntity(entity) && IsEntityTypePickable(entity);
    }
    private bool CanSelectSceneEntity(Entity entity)
    {
        if (!RuntimeSourceAvailable) return false;
        var owner = RuntimeOwner(entity);
        return owner != null && EntityLocked?.Invoke(owner, entity.Id) != true
            && (owner is not SceneDocument scene || SceneLoaded?.Invoke(scene) != false && SceneLocked?.Invoke(scene) != true);
    }
    private readonly record struct ChildSceneProbeState(AssetId Id, Guid? Generation, SceneSynchronizationState State);
    private readonly Dictionary<AssetId, ChildSceneProbeState> observedChildSceneStates = [];
    private long childStateVersion;
    internal long CachedChildSceneStateVersion => childStateVersion;
    private long remappedRootStructure = -1;
    private long observedPresentationVersion = -1;
    internal long ChildSceneStateVersion
    {
        get
        {
            var presentationVersion = ScenePresentationVersion?.Invoke() ?? 0;
            var changed = observedChildSceneStates.Count != childSceneProbes.Count || observedPresentationVersion != presentationVersion;
            observedPresentationVersion = presentationVersion;
            foreach (var pair in childSceneProbes)
            {
                var owner = pair.Value.Document.SessionHistory?.Registry.Find(pair.Key);
                var document = owner?.Document as SceneDocument ?? pair.Value.Document;
                var current = new ChildSceneProbeState(pair.Key, owner?.Generation, document.SynchronizationState);
                if (observedChildSceneStates.TryGetValue(pair.Key, out var previous) && current == previous) continue;
                observedChildSceneStates[pair.Key] = current; changed = true;
            }
            if (changed)
            {
                foreach (var id in observedChildSceneStates.Keys.Where(id => !childSceneProbes.ContainsKey(id)).ToArray()) observedChildSceneStates.Remove(id);
                childStateVersion++;
            }
            return childStateVersion;
        }
    }
    private void SynchronizeChildScenes(EntityHierarchyDocument root)
    {
        if (loadedScene == null) return;
        if (root is not SceneDocument source)
        { if (childScenes.Count > 0 || childSceneProbes.Count > 0) ClearChildScenes(); return; }
        // The editor owns the displayed root anchor; ancestors still contribute their authored offsets.
        loadedScene.Parent = null;
        loadedScene.Offset = ResolveSceneWorldOffset?.Invoke(root) ?? source.Asset.Offset;
        var desired = desiredChildScenes; desired.Clear();
        var visiting = visitingChildScenes; visiting.Clear(); visiting.Add(source.Asset.Id);
        void Collect(SceneDocument parent)
        {
            foreach (var id in parent.Asset.ChildrenIds)
            {
                if (!visiting.Add(id)) throw new InvalidOperationException("Circular or duplicated scene hierarchy");
                var child = ResolveChildSceneDocument?.Invoke(id) ?? throw new InvalidOperationException("The child scene is not available.");
                if (SceneLoaded?.Invoke(child) == false) continue;
                if (!childSceneProbes.TryGetValue(id, out var probe) || !ReferenceEquals(probe.Document, child))
                {
                    var owner = child.SessionHistory?.Registry.Find(id)
                        ?? throw new InvalidOperationException("The child scene has no canonical authored owner.");
                    var lease = owner.AcquireView();
                    if (probe.Lease != null) probe.Lease.Dispose();
                    childSceneProbes[id] = (child, lease);
                }
                var parentReference = AttachedReferenceManager.GetAttachedReference(child.Asset.Parent);
                if (parentReference?.Id != parent.Asset.Id) throw new InvalidOperationException("The child scene parent relationship is inconsistent.");
                desired.Add(id, (child, parent.Asset.Id)); Collect(child);
            }
        }
        if (SceneLoaded?.Invoke(source) != false) Collect(source);
        removedChildSceneIds.Clear(); foreach (var id in childSceneProbes.Keys) if (!desired.ContainsKey(id)) removedChildSceneIds.Add(id);
        foreach (var id in removedChildSceneIds)
        { childSceneProbes[id].Lease.Dispose(); childSceneProbes.Remove(id); }
        var prepared = preparedChildScenes; prepared.Clear();
        foreach (var pair in desired)
            if (childScenes.TryGetValue(pair.Key, out var live) && ReferenceEquals(live.Document, pair.Value.Document)
                && live.StructureVersion == live.Document.StructureVersion
                && live.State != live.Document.SynchronizationState)
            {
                if (live.State.Revision != live.Document.SynchronizationState.Revision)
                    foreach (var part in live.Document.Asset.Hierarchy.Parts.Values) foreach (var component in part.Entity.Components)
                        live.Document.GetComponentFields(component);
                SynchronizeLiveChildValues(live);
                childScenes[pair.Key] = live with { State = live.Document.SynchronizationState };
            }
        var refreshReferences = remappedRootStructure != root.StructureVersion
            || childScenes.Keys.Any(id => !desired.ContainsKey(id))
            || desired.Any(pair => !childScenes.TryGetValue(pair.Key, out var current)
                || !ReferenceEquals(current.Document, pair.Value.Document)
                || current.State != pair.Value.Document.SynchronizationState);
        try
        {
            foreach (var (id, wanted) in desired)
            {
                if (childScenes.TryGetValue(id, out var unchanged) && ReferenceEquals(unchanged.Document, wanted.Document)
                    && unchanged.State == wanted.Document.SynchronizationState && unchanged.StructureVersion == wanted.Document.StructureVersion) continue;
                var state = wanted.Document.SessionHistory?.Registry.Find(id)
                    ?? throw new InvalidOperationException("The child scene has no canonical authored owner.");
                var lease = state.AcquireView();
                try
                {
                    var graph = wanted.Document.HierarchyGraph;
                    var external = ExternalReferenceCollector.GetExternalReferences(graph.Definition, graph.RootNode);
                    var clone = (SceneAsset)AssetCloner.Clone(wanted.Document.Asset, AssetClonerFlags.None, external, out _)!;
                    var scene = new Scene { Offset = wanted.Document.Asset.Offset };
                    var entities = clone.Hierarchy.Parts.Values.ToDictionary(part => part.Entity.Id, part => part.Entity);
                    foreach (var entity in clone.Hierarchy.RootParts) scene.Entities.Add(entity);
                    foreach (var entity in entities.Values)
                    foreach (var component in entity.Components)
                    {
                        if (component is CameraComponent camera) camera.Enabled = false;
                        var componentReferences = new ComponentReferenceCollector(component, external.Select(value => value.Id).ToHashSet());
                        componentReferences.Visit(component);
                        foreach (var reference in componentReferences.Content)
                        {
                            var proxy = reference.Path.GetValue(component);
                            if (!reference.Path.Apply(component, MemberPathAction.ValueSet, ResolveRuntimeContentReference(proxy)))
                                throw new InvalidOperationException("Unable to resolve a child scene content reference.");
                        }
                    }
                    var candidate = new ChildSceneRuntime(wanted.Document, lease, scene, entities, wanted.Document.SynchronizationState, wanted.Document.StructureVersion,
                        wanted.Document.Asset.Hierarchy.Parts.Values.SelectMany(part => part.Entity.Components)
                            .ToDictionary(component => component.Id, component => wanted.Document.ComponentReplacementVersion(component.Id)));
                    prepared.Add(id, PreserveChildHierarchyInstances(unchanged, candidate));
                }
                catch { lease.Dispose(); throw; }
            }
            // Actual reference remapping/topology commits below include retained owners too.
        }
        catch { foreach (var preparedScene in prepared.Values) preparedScene.Lease.Dispose(); throw; }
        var combined = refreshReferences ? desired.ToDictionary(pair => pair.Key, pair => prepared.GetValueOrDefault(pair.Key) ?? childScenes[pair.Key]) : childScenes;
        var snapshots = prepared.Count == 0 ? Array.Empty<ChildHierarchySnapshot>() : prepared.Where(pair => childScenes.TryGetValue(pair.Key, out var previous) && ReferenceEquals(previous.Scene, pair.Value.Scene))
            .Select(pair => new ChildHierarchySnapshot(childScenes[pair.Key])).ToArray();
        var previousModels = prepared.Count == 0 ? Array.Empty<ModelComponent>() : prepared.Keys.Where(childScenes.ContainsKey).SelectMany(id => childScenes[id].Entities.Values)
            .SelectMany(entity => entity.Components.OfType<ModelComponent>()).ToArray();
        IReadOnlyList<ChildRuntimeReference> references;
        try { references = refreshReferences ? PrepareChildSceneReferences(source, combined) : []; }
        catch { foreach (var entry in prepared.Values) entry.Lease.Dispose(); throw; }
        var referenceCount = 0;
        try
        {
            foreach (var pair in prepared)
                if (childScenes.TryGetValue(pair.Key, out var previous) && ReferenceEquals(previous.Scene, pair.Value.Scene))
                {
                    foreach (var entity in previous.Entities.Values) Picker?.ForgetGizmo(entity);
                    ApplyPreservedChildHierarchy(previous, pair.Value);
                }
            foreach (var reference in references) { referenceCount++; reference.Apply(true); }
        }
        catch (Exception failure)
        {
            try
            {
                for (var index = referenceCount - 1; index >= 0; index--) references[index].Apply(false);
                foreach (var snapshot in snapshots.Reverse()) snapshot.Restore();
                foreach (var id in prepared.Keys) if (childScenes.TryGetValue(id, out var entry)) foreach (var entity in entry.Entities.Values) Picker?.CacheEntity(entity);
            }
            catch (Exception recovery) { throw new AggregateException("Child hierarchy synchronization and restoration failed.", failure, recovery); }
            finally { foreach (var entry in prepared.Values) entry.Lease.Dispose(); }
            throw;
        }
        var topologyChanged = prepared.Count > 0 || childScenes.Keys.Any(id => !desired.ContainsKey(id));
        if (topologyChanged) selectionRuntimeVersion++;
        if (topologyChanged) Picker?.Cancel("Child scene changed");
        foreach (var model in previousModels) modelUrls.Remove(model);
        removedChildSceneIds.Clear();
        foreach (var id in childScenes.Keys)
            if (!desired.ContainsKey(id) || prepared.TryGetValue(id, out var next) && !ReferenceEquals(next.Scene, childScenes[id].Scene)) removedChildSceneIds.Add(id);
        foreach (var id in removedChildSceneIds)
            RemoveChildScene(id);
        foreach (var pair in prepared)
        {
            if (childScenes.TryGetValue(pair.Key, out var previous))
            {
                foreach (var entity in previous.Entities.Values) childPickingOwners.Remove(entity);
                foreach (var entity in previous.Entities.Values) foreach (var model in entity.Components.OfType<ModelComponent>()) modelUrls.Remove(model);
                previous.Lease.Dispose();
            }
            childScenes[pair.Key] = pair.Value with { ComponentPlan = null };
        }
        foreach (var entry in prepared.Values)
            foreach (var entity in entry.Entities.Values)
                foreach (var model in entity.Components.OfType<ModelComponent>())
                {
                    var authoredModel = entry.Document.Asset.Hierarchy.Parts[entity.Id].Entity.Components.OfType<ModelComponent>().First(value => value.Id == model.Id);
                    if (authoredModel.Model != null && AttachedReferenceManager.GetAttachedReference(authoredModel.Model) is { } reference) modelUrls[model] = reference.Url;
                }
        remappedRootStructure = root.StructureVersion;
        foreach (var pair in desired)
        {
            var entry = childScenes[pair.Key];
            var parent = pair.Value.Parent == source.Asset.Id ? loadedScene : childScenes[pair.Value.Parent].Scene;
            if (!ReferenceEquals(entry.Scene.Parent, parent)) entry.Scene.Parent = parent;
            entry.Scene.Offset = entry.Document.Asset.Offset;
            if (topologyChanged)
                foreach (var entity in entry.Entities.Values)
                { childPickingOwners[entity] = entry.Document; Picker?.CacheEntity(entity); }
        }
        // Keep real child Scene order after the editor's grid/gizmo child scenes.
        void OrderChildren(Scene parent, IReadOnlyList<AssetId> ids)
        {
            if (ids.Count == 0) return;
            var loaded = ids.Where(childScenes.ContainsKey).ToArray();
            var start = parent.Children.Count - loaded.Length;
            for (var index = 0; index < loaded.Length; index++)
            {
                var child = childScenes[loaded[index]].Scene;
                if (parent.Children.IndexOf(child) == start + index) continue;
                parent.Children.Remove(child); parent.Children.Insert(start + index, child);
            }
        }
        if (topologyChanged || refreshReferences)
        {
            foreach (var pair in desired) OrderChildren(childScenes[pair.Key].Scene, pair.Value.Document.Asset.ChildrenIds);
            OrderChildren(loadedScene, source.Asset.ChildrenIds);
        }
        prepared.Clear();
    }
    private List<ChildRuntimeReference> PrepareChildSceneReferences(SceneDocument root, IReadOnlyDictionary<AssetId, ChildSceneRuntime> scenes)
    {
        var objects = new Dictionary<IIdentifiable, IIdentifiable>(ReferenceEqualityComparer.Instance);
        var ownerIdentities = new Dictionary<AssetId, Dictionary<Guid, IIdentifiable>>();
        void AddObjects(EntityHierarchyDocument document, Dictionary<Guid, Entity> entities)
        {
            var identities = new Dictionary<Guid, IIdentifiable>(); ownerIdentities.Add(document.Asset.Id, identities);
            foreach (var part in document.Asset.Hierarchy.Parts.Values)
            {
                if (!entities.TryGetValue(part.Entity.Id, out var runtime)) continue;
                objects[part.Entity] = runtime;
                identities[part.Entity.Id] = runtime;
                foreach (var source in part.Entity.Components)
                {
                    var component = ChildRuntimeComponents(scenes.GetValueOrDefault(document.Asset.Id), runtime).FirstOrDefault(value => value.Id == source.Id);
                    if (component == null) continue;
                    objects[source] = component;
                    identities[source.Id] = component;
                    var visitor = new ComponentReferenceCollector(source, document.GetComponentExternalReferences(source).Select(value => value.Id).ToHashSet());
                    visitor.Visit(source);
                    foreach (var owned in visitor.Owned)
                        if (owned.Path.GetValue(source) is IIdentifiable original && owned.Path.GetValue(component) is IIdentifiable value)
                        { objects[original] = value; identities[original.Id] = value; }
                }
            }
        }
        AddObjects(root, runtimeEntities);
        foreach (var entry in scenes.Values) AddObjects(entry.Document, entry.Entities);
        var runtimeValues = objects.Values.ToHashSet(ReferenceEqualityComparer.Instance);
        var changes = new List<ChildRuntimeReference>();
        foreach (var entry in scenes.Values)
        foreach (var entity in entry.Entities.Values)
        foreach (var component in ChildRuntimeComponents(entry, entity))
        {
            var source = entry.Document.Asset.Hierarchy.Parts[entity.Id].Entity.Components.First(value => value.Id == component.Id);
            var visitor = new ComponentReferenceCollector(component,
                entry.Document.GetComponentExternalReferences(source).Select(value => value.Id).ToHashSet());
            visitor.Visit(component);
            foreach (var reference in visitor.Scene)
            {
                if (reference.Value is not IIdentifiable value || runtimeValues.Contains(value)) continue;
                if (component is TransformComponent && value is TransformComponent) continue;
                var target = objects.GetValueOrDefault(value) ?? ownerIdentities[entry.Document.Asset.Id].GetValueOrDefault(value.Id);
                if (target == null && (value is Entity referencedEntity ? RuntimeOwner(referencedEntity) : value is EntityComponent targetComponent ? RuntimeOwner(targetComponent.Entity) : null) is { } previousOwner)
                    target = ownerIdentities.GetValueOrDefault(previousOwner.Asset.Id)?.GetValueOrDefault(value.Id);
                if (target == null)
                {
                    IIdentifiable? resolved = null; var ambiguous = false;
                    foreach (var scope in ownerIdentities.Values)
                        if (scope.TryGetValue(value.Id, out var possible))
                        { if (resolved != null && !ReferenceEquals(resolved, possible)) { ambiguous = true; break; } resolved = possible; }
                    if (!ambiguous) target = resolved;
                }
                changes.Add(new(component, reference.Path, value, target));
            }
        }
        return changes;
    }
    private void SynchronizeLiveChildValues(ChildSceneRuntime entry)
    {
        foreach (var part in entry.Document.Asset.Hierarchy.Parts.Values)
            if (entry.Entities.TryGetValue(part.Entity.Id, out var runtime))
            { TransformState.Read(part.Entity).Apply(runtime); runtime.Name = part.Entity.Name; }
        object? Map(object? value)
        {
            var sourceEntity = value is Entity entity ? entity : (value as EntityComponent)?.Entity;
            if (sourceEntity == null) return null;
            var owner = PresentedDocument != null && PresentedDocument.Asset.Hierarchy.Parts.TryGetValue(sourceEntity.Id, out var rootPart)
                && ReferenceEquals(rootPart.Entity, sourceEntity) ? PresentedDocument
                : childScenes.Values.FirstOrDefault(child => child.Document.Asset.Hierarchy.Parts.TryGetValue(sourceEntity.Id, out var part)
                    && ReferenceEquals(part.Entity, sourceEntity))?.Document;
            var target = ResolveRuntimeEntity(owner, sourceEntity.Id);
            return value is EntityComponent source ? target?.Components.FirstOrDefault(component => component.Id == source.Id) : target;
        }
        foreach (var field in entry.Document.ObservedComponentFields)
        {
            if (!entry.Entities.TryGetValue(field.Component.Entity.Id, out var owner)) continue;
            var component = FindObservedRuntimeComponent(owner, field.Component.Id);
            if (component == null || component is CameraComponent && field.Name == nameof(CameraComponent.Enabled)) continue;
            var value = field.Node.Retrieve();
            if (field.IsContentReference) value = ResolveRuntimeContentReference(value);
            if (field.IsSceneReference) value = Map(value);
            if (Equals(field.ReadRuntime(component), value)) continue;
            Picker?.Cancel("Child scene component changed"); field.WriteRuntime(component, value);
            NotifyRuntimeComponentPropertyChanged(field, component);
            if (field.IsContentReference) NotifyRuntimeContentReferenceChanged(field, component, value);
        }
    }
    private void RemoveChildScene(AssetId id, SceneViewportResources? retirement = null)
    {
        var entry = childScenes[id];
        foreach (var entity in entry.Entities.Values) childPickingOwners.Remove(entity);
        foreach (var entity in entry.Entities.Values) foreach (var model in entity.Components.OfType<ModelComponent>()) modelUrls.Remove(model);
        foreach (var root in entry.Scene.Entities) Picker?.ForgetGizmo(root);
        // A retiring native scene keeps its whole processor/collider graph alive until
        // its real builder workers and submitted GPU users have drained.
        if (retirement == null) entry.Scene.Parent = null;
        if (retirement != null) retirement.RetainLease(entry.Lease); else entry.Lease.Dispose();
        childScenes.Remove(id);
    }
    private void ClearChildScenes(SceneViewportResources? retirement = null)
    {
        var attachedLeases = retirement == null
            ? new HashSet<IDisposable>(childScenes.Values.Select(child => child.Lease).Concat(childSceneProbes.Values.Select(probe => probe.Lease)), ReferenceEqualityComparer.Instance)
            : null;
        foreach (var id in childScenes.Keys.ToArray()) RemoveChildScene(id, retirement);
        foreach (var probe in childSceneProbes.ToArray())
        {
            if (retirement != null) retirement.RetainLease(probe.Value.Lease); else probe.Value.Lease.Dispose();
            childSceneProbes.Remove(probe.Key);
        }
        foreach (var prepared in preparedChildScenes.ToArray())
        {
            if (retirement != null) retirement.RetainLease(prepared.Value.Lease);
            else if (!attachedLeases!.Contains(prepared.Value.Lease)) prepared.Value.Lease.Dispose();
            preparedChildScenes.Remove(prepared.Key);
        }
        desiredChildScenes.Clear(); preparedChildScenes.Clear(); visitingChildScenes.Clear(); removedChildSceneIds.Clear();
        childPickingOwners.Clear(); observedChildSceneStates.Clear(); remappedRootStructure = -1; childStateVersion++;
    }
}
