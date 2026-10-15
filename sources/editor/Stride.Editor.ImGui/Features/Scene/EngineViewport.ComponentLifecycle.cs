using Stride.Core.Assets;
using Stride.Core;
using Stride.Core.Assets.Visitors;
using Stride.Core.Reflection;
using Stride.Core.Serialization;
using Stride.Engine;
using Stride.Rendering;

namespace Stride.Editor.ImGui;

internal sealed partial class EngineViewport
{
    private readonly Dictionary<(Type Type, string Url), object> componentContent = [];
    private readonly Dictionary<Guid, long> runtimeComponentReplacementVersions = [];

    private void SynchronizeComponentCollections(EntityHierarchyDocument document)
    {
        // Prepare all clones and content before touching live entity collections.
        // References are mapped only after every new component has an engine owner.
        var additions = new List<(Entity Owner, EntityComponent Component, ComponentReferenceCollector References)>();
        foreach (var design in document.Asset.Hierarchy.Parts.Values)
        {
            if (!runtimeEntities.TryGetValue(design.Entity.Id, out var runtime)) continue;
            foreach (var source in design.Entity.Components)
            {
                if (runtime.Components.Any(component => component.Id == source.Id)
                    && runtimeComponentReplacementVersions.GetValueOrDefault(source.Id) == document.ComponentReplacementVersion(source.Id)) continue;
                var external = document.GetComponentExternalReferences(source);
                var clone = (EntityComponent)AssetCloner.Clone(source, AssetClonerFlags.None,
                    external, out _)!;
                var references = new ComponentReferenceCollector(clone, external.Select(value => value.Id).ToHashSet());
                references.Visit(clone);
                foreach (var reference in references.Content)
                {
                    var key = (reference.Type, reference.Url);
                    if (!componentContent.TryGetValue(key, out var value))
                    {
                        value = content!.Load(reference.Type, ResolveRuntimeComponentContentUrl(reference.Id, reference.Url));
                        componentContent.Add(key, value);
                    }
                    ApplyRuntimeTextureOverrides(value);
                    if (!reference.Path.Apply(clone, MemberPathAction.ValueSet, value))
                        throw new InvalidOperationException("Unable to resolve component content reference: " + reference.Url);
                }
                foreach (var reference in references.Scene)
                    if (!reference.Path.Apply(clone, MemberPathAction.ValueSet, null))
                        throw new InvalidOperationException("Unable to clear an authored reference from a runtime component.");
                if (clone is CameraComponent camera) camera.Enabled = false;
                additions.Add((runtime, clone, references));
            }
        }
        foreach (var design in document.Asset.Hierarchy.Parts.Values)
        {
            if (!runtimeEntities.TryGetValue(design.Entity.Id, out var runtime)) continue;
            foreach (var component in runtime.Components.Where(component => component is not TransformComponent
                         && (design.Entity.Components.All(source => source.Id != component.Id)
                             || additions.Any(addition => addition.Owner == runtime && addition.Component.Id == component.Id))).ToArray())
            {
                if (component is ModelComponent model) modelUrls.Remove(model);
                runtime.Components.Remove(component);
            }
        }
        foreach (var addition in additions)
        {
            addition.Owner.Add(addition.Component);
            ApplyRuntimeTextureOverrides(addition.Component);
            runtimeComponentReplacementVersions[addition.Component.Id] = document.ComponentReplacementVersion(addition.Component.Id);
            if (addition.Component is ModelComponent model)
            {
                var source = document.Asset.Hierarchy.Parts[addition.Owner.Id].Entity.Components.Single(component => component.Id == model.Id) as ModelComponent;
                if (AttachedReferenceManager.GetAttachedReference(source?.Model) is { } reference) modelUrls[model] = reference.Url;
                Picker?.CacheEntity(addition.Owner);
            }
        }
        var runtimeObjects = new Dictionary<Guid, object>();
        var sourceReferences = new List<(EntityComponent Runtime, ComponentReferenceCollector References)>();
        foreach (var design in document.Asset.Hierarchy.Parts.Values)
        {
            if (!runtimeEntities.TryGetValue(design.Entity.Id, out var runtime)) continue;
            runtimeObjects[runtime.Id] = runtime;
            foreach (var source in design.Entity.Components)
            {
                var component = runtime.Components.Single(value => value.Id == source.Id);
                var external = document.GetComponentExternalReferences(source);
                var references = new ComponentReferenceCollector(source, external.Select(value => value.Id).ToHashSet());
                references.Visit(source); sourceReferences.Add((component, references));
                foreach (var owned in references.Owned)
                    if (owned.Path.GetValue(component) is { } value) runtimeObjects[owned.Id] = value;
            }
        }
        foreach (var addition in additions)
        foreach (var reference in addition.References.Scene)
        {
            var target = reference.Value is IIdentifiable identifiable ? runtimeObjects.GetValueOrDefault(identifiable.Id) : null;
            if (!reference.Path.Apply(addition.Component, MemberPathAction.ValueSet, target))
                throw new InvalidOperationException("Unable to resolve a runtime component scene reference.");
        }
        foreach (var design in document.Asset.Hierarchy.Parts.Values)
        {
            if (!runtimeEntities.TryGetValue(design.Entity.Id, out var runtime)) continue;
            for (var index = 1; index < design.Entity.Components.Count; index++)
            {
                var component = runtime.Components.First(candidate => candidate.Id == design.Entity.Components[index].Id);
                if (runtime.Components.IndexOf(component) == index) continue;
                runtime.Components.Remove(component); runtime.Components.Insert(index, component);
            }
        }
        // Refresh every external scene reference, including dictionary and nested
        // collection paths, after replacing a component that retained its ID.
        foreach (var entry in sourceReferences)
        {
            foreach (var reference in entry.References.Scene)
            {
                var value = reference.Value is IIdentifiable identifiable ? runtimeObjects.GetValueOrDefault(identifiable.Id) : null;
                if (!reference.Path.Apply(entry.Runtime, MemberPathAction.ValueSet, value))
                    throw new InvalidOperationException("Unable to synchronize a component scene reference.");
            }
        }
    }

    private void ClearComponentContent(SceneViewportResources? retirement = null)
    {
        foreach (var entry in componentContent.ToArray())
        {
            if (retirement == null && !borrowedComponentContent.Contains(entry.Key)) content?.Unload(entry.Value);
            componentContent.Remove(entry.Key);
        }
        borrowedComponentContent.Clear();
        ClearRuntimeTextureOverrides();
        runtimeComponentReplacementVersions.Clear();
    }

    private sealed class ComponentReferenceCollector(EntityComponent root, HashSet<Guid> externalIds) : AssetVisitorBase
    {
        internal readonly List<(MemberPath Path, Type Type, AssetId Id, string Url)> Content = [];
        internal readonly List<(MemberPath Path, object Value)> Scene = [];
        internal readonly List<(MemberPath Path, Guid Id)> Owned = [];
        protected override bool CanVisit(object value) => value is Entity or EntityComponent && !ReferenceEquals(value, root)
            || value is IIdentifiable identifiable && externalIds.Contains(identifiable.Id)
            || AttachedReferenceManager.GetAttachedReference(value) != null || base.CanVisit(value);
        public override void VisitObjectMember(object container, ObjectDescriptor descriptor, IMemberDescriptor member, object? value)
        {
            if (container is TransformComponent && member.Name == nameof(TransformComponent.Children)) return;
            base.VisitObjectMember(container, descriptor, member, value);
        }
        public override void VisitObject(object value, ObjectDescriptor descriptor, bool visitMembers)
        {
            if (AttachedReferenceManager.GetAttachedReference(value) is { } reference)
            {
                Content.Add((CurrentPath.Clone(), value.GetType(), reference.Id, reference.Url)); return;
            }
            if (value is Entity || value is EntityComponent && !ReferenceEquals(value, root)
                || value is IIdentifiable external && externalIds.Contains(external.Id))
            {
                Scene.Add((CurrentPath.Clone(), value)); return;
            }
            if (value is IIdentifiable owned) Owned.Add((CurrentPath.Clone(), owned.Id));
            base.VisitObject(value, descriptor, visitMembers);
        }
    }
}
