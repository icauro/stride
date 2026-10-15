using Stride.Core;
using Stride.Core.Reflection;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal sealed partial class EngineViewport
{
    private static ChildSceneRuntime PreserveChildHierarchyInstances(ChildSceneRuntime? previous, ChildSceneRuntime candidate)
    {
        if (previous == null || !ReferenceEquals(previous.Document, candidate.Document)) return candidate;
        var components = new Dictionary<Guid, EntityComponent[]>();
        foreach (var pair in candidate.Entities)
        {
            if (!previous.Entities.TryGetValue(pair.Key, out var existing)) { components.Add(pair.Key, pair.Value.Components.ToArray()); continue; }
            if (existing.Transform.Id != pair.Value.Transform.Id) return candidate;
            components.Add(pair.Key, pair.Value.Components.Select(next =>
            {
                var retained = existing.Components.FirstOrDefault(component => component.Id == next.Id && component.GetType() == next.GetType());
                return retained != null && (retained is TransformComponent
                    || previous.ComponentVersions.GetValueOrDefault(retained.Id) == candidate.ComponentVersions.GetValueOrDefault(retained.Id)) ? retained : next;
            }).ToArray());
        }
        var entities = candidate.Entities.ToDictionary(pair => pair.Key,
            pair => previous.Entities.GetValueOrDefault(pair.Key) ?? pair.Value);
        return candidate with { Scene = previous.Scene, Entities = entities, ComponentPlan = components, State = previous.State };
    }
    private static IEnumerable<EntityComponent> ChildRuntimeComponents(ChildSceneRuntime? entry, Entity entity)
        => entry?.ComponentPlan?.GetValueOrDefault(entity.Id) ?? (IEnumerable<EntityComponent>)entity.Components;
    private static void ApplyPreservedChildHierarchy(ChildSceneRuntime previous, ChildSceneRuntime next)
    {
        // New cloned entities must not introduce discarded clones of retained children.
        foreach (var entity in next.Entities.Values)
            if (!ReferenceEquals(entity, previous.Entities.GetValueOrDefault(entity.Id)))
            {
                foreach (var child in entity.Transform.Children.ToArray()) child.Parent = null;
                entity.Transform.Parent = null; entity.Scene = null;
            }
        foreach (var entity in previous.Entities.Values)
            if (!next.Entities.ContainsKey(entity.Id))
            {
                entity.Transform.Parent = null;
                if (ReferenceEquals(entity.Scene, previous.Scene)) entity.Scene = null;
            }
        foreach (var part in next.Document.Asset.Hierarchy.Parts.Values)
        {
            var entity = next.Entities[part.Entity.Id];
            var components = ChildRuntimeComponents(next, entity).ToArray();
            foreach (var component in entity.Components.ToArray())
                if (component is not TransformComponent && !components.Contains(component)) entity.Components.Remove(component);
            foreach (var component in components)
                if (!entity.Components.Contains(component))
                {
                    component.Entity?.Components.Remove(component);
                    entity.Add(component);
                }
            for (var index = 1; index < components.Length; index++)
                if (entity.Components.IndexOf(components[index]) != index)
                { entity.Components.Remove(components[index]); entity.Components.Insert(index, components[index]); }
            var parent = part.Entity.Transform.Parent is { } sourceParent ? next.Entities[sourceParent.Entity.Id].Transform : null;
            if (!ReferenceEquals(entity.Transform.Parent, parent)) entity.Transform.Parent = parent;
            if (parent == null && !ReferenceEquals(entity.Scene, next.Scene)) next.Scene.Entities.Add(entity);
            entity.Name = part.Entity.Name; TransformState.Read(part.Entity).Apply(entity);
        }
        foreach (var entity in next.Scene.Entities.ToArray())
            if (!next.Entities.TryGetValue(entity.Id, out var expected) || !ReferenceEquals(expected, entity) || entity.Transform.Parent != null)
                next.Scene.Entities.Remove(entity);
        for (var index = 0; index < next.Document.Asset.Hierarchy.RootParts.Count; index++)
        {
            var entity = next.Entities[next.Document.Asset.Hierarchy.RootParts[index].Id];
            if (next.Scene.Entities.IndexOf(entity) != index) { next.Scene.Entities.Remove(entity); next.Scene.Entities.Insert(index, entity); }
        }
        foreach (var part in next.Document.Asset.Hierarchy.Parts.Values)
        {
            var parent = next.Entities[part.Entity.Id];
            for (var index = 0; index < part.Entity.Transform.Children.Count; index++)
            {
                var child = next.Entities[part.Entity.Transform.Children[index].Entity.Id];
                if (parent.Transform.Children.IndexOf(child.Transform) == index) continue;
                parent.Transform.Children.Remove(child.Transform); parent.Transform.Children.Insert(index, child.Transform);
            }
        }
    }
    private sealed class ChildHierarchySnapshot
    {
        private readonly ChildSceneRuntime entry;
        private readonly Entity[] roots;
        private readonly (Entity Entity, Entity? Parent, string Name, TransformState Transform, Entity[] Children, EntityComponent[] Components)[] values;
        internal ChildHierarchySnapshot(ChildSceneRuntime source)
        {
            entry = source; roots = source.Scene.Entities.ToArray();
            values = source.Entities.Values.Select(entity => (entity, entity.Transform.Parent?.Entity, entity.Name, TransformState.Read(entity),
                entity.Transform.Children.Select(child => child.Entity).ToArray(), entity.Components.ToArray())).ToArray();
        }
        internal void Restore()
        {
            foreach (var root in entry.Scene.Entities.ToArray())
            {
                void Detach(Entity entity)
                {
                    foreach (var child in entity.Transform.Children.ToArray()) Detach(child.Entity);
                    entity.Transform.Parent = null; entity.Scene = null;
                }
                Detach(root);
            }
            foreach (var value in values) value.Entity.Transform.Parent = null;
            entry.Scene.Entities.Clear();
            foreach (var root in roots) entry.Scene.Entities.Add(root);
            foreach (var value in values)
            {
                foreach (var component in value.Entity.Components.ToArray())
                    if (component is not TransformComponent && !value.Components.Contains(component)) value.Entity.Components.Remove(component);
                foreach (var component in value.Components)
                    if (!value.Entity.Components.Contains(component))
                    { component.Entity?.Components.Remove(component); value.Entity.Add(component); }
                for (var index = 1; index < value.Components.Length; index++)
                    if (value.Entity.Components.IndexOf(value.Components[index]) != index)
                    { value.Entity.Components.Remove(value.Components[index]); value.Entity.Components.Insert(index, value.Components[index]); }
                value.Entity.Transform.Parent = value.Parent?.Transform;
                value.Entity.Name = value.Name; value.Transform.Apply(value.Entity);
            }
            foreach (var value in values) for (var index = 0; index < value.Children.Length; index++)
            {
                var child = value.Children[index].Transform;
                if (value.Entity.Transform.Children.IndexOf(child) == index) continue;
                value.Entity.Transform.Children.Remove(child); value.Entity.Transform.Children.Insert(index, child);
            }
        }
    }
    private sealed record ChildRuntimeReference(object Owner, MemberPath Path, object? Before, object? After)
    {
        internal void Apply(bool forward)
        {
            if (!Path.Apply(Owner, MemberPathAction.ValueSet, forward ? After : Before))
                throw new InvalidOperationException("Unable to synchronize a child scene runtime reference.");
        }
    }
}
