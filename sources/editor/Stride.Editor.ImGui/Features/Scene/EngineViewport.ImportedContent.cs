using Stride.Animations;
using Stride.Core.Serialization;
using Stride.Engine;
using Stride.Rendering;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed partial class EngineViewport
{
    internal readonly record struct ImportedContent(Type Type, string Url, object Value);
    private IEnumerable<(EntityHierarchyDocument Owner, IReadOnlyDictionary<Guid, Entity> Entities)> ImportRuntimeOwners(EntityHierarchyDocument? document)
    {
        if (document != null) yield return (document, runtimeEntities);
        foreach (var scene in childScenes.Values) yield return (scene.Document, scene.Entities);
        foreach (var scene in preparedChildScenes.Values) yield return (scene.Document, scene.Entities);
    }
    private Entity[] ImportRuntimeEntities(EntityHierarchyDocument? document)
        => runtimeEntities.Values.Concat(ImportRuntimeOwners(document).SelectMany(owner => owner.Entities.Values))
            .Distinct<Entity>(ReferenceEqualityComparer.Instance).ToArray();

    internal void ReplaceRuntimeImportedContent(IReadOnlyList<ImportedContent> replacements, EntityHierarchyDocument? document)
    {
        var changes = new List<CompiledTextureConsumers.Change>();
        var runtimeOwners = ImportRuntimeOwners(document).ToArray();
        var entities = ImportRuntimeEntities(document);
        foreach (var replacement in replacements)
        {
            if (replacement.Value is Material material) PrepareImportedMaterialConsumers(replacement.Url, material, document, changes);
            var key = (replacement.Type, replacement.Url);
            var existed = componentContent.TryGetValue(key, out var cached);
            var borrowed = borrowedComponentContent.Contains(key);
            foreach (var owner in runtimeOwners) foreach (var field in owner.Owner.ObservedComponentFields)
            {
                if (!field.IsContentReference || !field.Node.MemberDescriptor.HasSet
                    || !field.Type.IsInstanceOfType(replacement.Value)
                    || !owner.Entities.TryGetValue(field.Component.Entity.Id, out var entity)) continue;
                var component = entity.Components.FirstOrDefault(candidate => candidate.Id == field.Component.Id);
                if (component is AudioEmitterComponent && replacement.Value is Stride.Audio.Sound) continue;
                if (component == null || field.ReadRuntime(component) is not { } previous
                    || AttachedReferenceManager.GetUrl(previous) != replacement.Url) continue;
                changes.Add(new(() => field.WriteRuntime(component, replacement.Value), () => field.WriteRuntime(component, previous)));
            }
            foreach (var pair in modelUrls)
                if (replacement.Value is Model model && pair.Value == replacement.Url)
                {
                    var component = pair.Key; var previous = component.Model;
                    changes.Add(new(() => component.Model = model, () => component.Model = previous));
                }
            foreach (var entity in entities)
            {
                foreach (var component in entity.Components)
                {
                    // Audio aliases/controllers have a stronger native playback contract below.
                    if (component is AudioEmitterComponent) continue;
                    var visitor = new ImportedRuntimeReferences(); visitor.Visit(component);
                    foreach (var reference in visitor.References)
                    {
                        if (!reference.CanWrite || !reference.StorageType.IsInstanceOfType(replacement.Value)
                            || AttachedReferenceManager.GetUrl(reference.Value) != replacement.Url) continue;
                        var path = reference.Path; var previous = reference.Value;
                        changes.Add(new(() =>
                        {
                            if (!path.Apply(component, MemberPathAction.ValueSet, replacement.Value))
                                throw new InvalidOperationException("A runtime content reference could not be updated.");
                        }, () =>
                        {
                            if (!path.Apply(component, MemberPathAction.ValueSet, previous))
                                throw new InvalidOperationException("A runtime content reference could not be restored.");
                        }));
                    }
                }
                foreach (var animation in entity.Components.OfType<AnimationComponent>())
                    if (replacement.Value is AnimationClip clip)
                    {
                        foreach (var pair in animation.Animations.ToArray())
                            if (AttachedReferenceManager.GetUrl(pair.Value) == replacement.Url)
                            {
                                var name = pair.Key; var previous = pair.Value;
                                changes.Add(new(() => animation.Animations[name] = clip, () => animation.Animations[name] = previous));
                            }
                        foreach (var playing in animation.PlayingAnimations.ToArray())
                            if (AttachedReferenceManager.GetUrl(playing.Clip) == replacement.Url)
                            {
                                var previous = playing.Clip;
                                changes.Add(new(() => animation.ReplacePlayingAnimationClip(playing, clip),
                                    () => animation.ReplacePlayingAnimationClip(playing, previous)));
                            }
                    }
                foreach (var physics in entity.Components.OfType<PhysicsComponent>())
                    for (var index = 0; index < physics.ColliderShapes.Count; index++)
                    {
                        var shapeIndex = index; var description = physics.ColliderShapes[index];
                        void Update(Action apply, Action undo) => changes.Add(new(
                            () => { apply(); physics.ColliderShapes[shapeIndex] = description; },
                            () => { undo(); physics.ColliderShapes[shapeIndex] = description; }));
                        if (description is Stride.Physics.ColliderShapeAssetDesc assetShape
                            && replacement.Value is Stride.Physics.PhysicsColliderShape collider
                            && assetShape.Shape != null && AttachedReferenceManager.GetUrl(assetShape.Shape) == replacement.Url)
                        {
                            var previous = assetShape.Shape;
                            Update(() => assetShape.Shape = collider, () => assetShape.Shape = previous);
                        }
                        if (description is Stride.Physics.HeightfieldColliderShapeDesc
                            { HeightStickArraySource: Stride.Physics.HeightStickArraySourceFromHeightmap heightSource }
                            && replacement.Value is Stride.Physics.Heightmap heightmap
                            && heightSource.Heightmap != null && AttachedReferenceManager.GetUrl(heightSource.Heightmap) == replacement.Url)
                        {
                            var previous = heightSource.Heightmap;
                            Update(() => heightSource.Heightmap = heightmap, () => heightSource.Heightmap = previous);
                        }
                        if (description is Stride.Physics.ConvexHullColliderShapeDesc hull
                            && replacement.Value is Model hullModel && hull.Model != null
                            && AttachedReferenceManager.GetUrl(hull.Model) == replacement.Url)
                        {
                            var previous = hull.Model;
                            Update(() => hull.Model = hullModel, () => hull.Model = previous);
                        }
                    }
                if (replacement.Value is Stride.Audio.Sound sound)
                    foreach (var emitter in entity.Components.OfType<AudioEmitterComponent>())
                        foreach (var previous in emitter.Sounds.Values.Where(previous => previous != null
                            && AttachedReferenceManager.GetUrl(previous) == replacement.Url).Distinct().ToArray())
                            changes.Add(new(() => emitter.ReplaceCompiledSound(previous, sound), () => emitter.ReplaceCompiledSound(sound, previous)));
            }
            changes.Add(new(() => { componentContent[key] = replacement.Value; borrowedComponentContent.Add(key); },
                () => { if (existed) componentContent[key] = cached!; else componentContent.Remove(key);
                    if (!borrowed) borrowedComponentContent.Remove(key); }));
        }
        CompiledTextureConsumers.Apply(changes);
        try
        {
            Picker?.Cancel("Imported content replaced");
            foreach (var entity in entities) Picker?.CacheEntity(entity);
        }
        catch (Exception failure)
        {
            var failures = new List<Exception> { failure };
            for (var index = changes.Count - 1; index >= 0; index--)
                try { changes[index].Undo(); } catch (Exception error) { failures.Add(error); }
            if (failures.Count > 1) throw new AggregateException("Imported content update could not be restored.", failures);
            throw;
        }
        selectionRuntimeVersion++;
        // Existing content-cache objects remain owned by the original cache until viewport teardown.
    }

    internal bool UsesImportedContent(IReadOnlyCollection<object> values, EntityHierarchyDocument? document)
    {
        var owned = new HashSet<object>(values, ReferenceEqualityComparer.Instance);
        foreach (var texture in values.OfType<Stride.Graphics.Texture>())
            if (UsesRuntimeTexture(texture, document)) return true;
        if (UsesImportedMaterial(values, document)) return true;
        if (componentContent.Values.Any(owned.Contains)) return true;
        foreach (var entity in ImportRuntimeEntities(document))
        {
            foreach (var component in entity.Components)
            {
                var visitor = new ImportedRuntimeReferences(); visitor.Visit(component);
                if (visitor.References.Any(reference => owned.Contains(reference.Value))) return true;
            }
            if (entity.Components.OfType<ModelComponent>().Any(component => component.Model != null && owned.Contains(component.Model))) return true;
            foreach (var animation in entity.Components.OfType<AnimationComponent>())
                if (animation.Animations.Values.Any(clip => owned.Contains(clip))
                    || animation.PlayingAnimations.Any(playing => owned.Contains(playing.Clip))) return true;
            foreach (var sprite in entity.Components.OfType<SpriteStudioComponent>())
                if (sprite.Sheet != null && owned.Contains(sprite.Sheet) || sprite.CurrentSheet != null && owned.Contains(sprite.CurrentSheet)) return true;
            foreach (var emitter in entity.Components.OfType<AudioEmitterComponent>())
                foreach (var sound in values.OfType<Stride.Audio.Sound>())
                    if (emitter.Sounds.Values.Any(value => ReferenceEquals(value, sound)) || emitter.ReferencesSound(sound)) return true;
            foreach (var physics in entity.Components.OfType<PhysicsComponent>())
            {
                foreach (var collider in values.OfType<Stride.Physics.PhysicsColliderShape>())
                    if (ContainsColliderShape(physics.ColliderShape, collider.Shape)) return true;
                foreach (var shape in physics.ColliderShapes)
                {
                    if (shape is Stride.Physics.ColliderShapeAssetDesc { Shape: { } assetShape } && owned.Contains(assetShape)) return true;
                    if (shape is Stride.Physics.HeightfieldColliderShapeDesc
                        { HeightStickArraySource: Stride.Physics.HeightStickArraySourceFromHeightmap { Heightmap: { } heightmap } }
                        && owned.Contains(heightmap)) return true;
                    if (shape is Stride.Physics.ConvexHullColliderShapeDesc { Model: { } model } && owned.Contains(model)) return true;
                }
            }
        }
        foreach (var owner in ImportRuntimeOwners(document)) foreach (var field in owner.Owner.ObservedComponentFields)
        {
            if (!field.IsContentReference || !owner.Entities.TryGetValue(field.Component.Entity.Id, out var entity)) continue;
            var component = entity.Components.FirstOrDefault(candidate => candidate.Id == field.Component.Id);
            if (component != null && field.ReadRuntime(component) is { } value && owned.Contains(value)) return true;
        }
        return false;
    }

    private static bool ContainsColliderShape(Stride.Physics.ColliderShape? root, Stride.Physics.ColliderShape? target)
    {
        if (root == null || target == null) return false;
        if (ReferenceEquals(root, target)) return true;
        if (root is Stride.Physics.CompoundColliderShape compound)
            for (var index = 0; index < compound.Count; index++)
                if (ContainsColliderShape(compound[index], target)) return true;
        return false;
    }
}
