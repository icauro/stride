using Stride.Core.Serialization;
using Stride.Engine;
using Stride.Rendering.Lights;
using Stride.Rendering.Skyboxes;

namespace Stride.Editor.ImGui;

internal sealed partial class EngineViewport
{
    internal void ReplaceRuntimeSkybox(string url, Skybox replacement, EntityHierarchyDocument? document)
    {
        var changes = new List<(Action Apply, Action Undo)>();
        var lights = new HashSet<LightSkybox>(ReferenceEqualityComparer.Instance);
        bool Matches(Skybox? skybox) => skybox != null
            && string.Equals(AttachedReferenceManager.GetUrl(skybox), url, StringComparison.Ordinal);
        void VisitEntity(Entity entity)
        {
            foreach (var component in entity.Components)
                if (component is LightComponent { Type: LightSkybox light } && lights.Add(light) && Matches(light.Skybox))
                {
                    var previous = light.Skybox;
                    changes.Add((() => light.Skybox = replacement, () => light.Skybox = previous));
                }
            foreach (var child in entity.Transform.Children) VisitEntity(child.Entity);
        }
        void VisitScene(Scene scene)
        {
            foreach (var entity in scene.Entities) VisitEntity(entity);
            foreach (var child in scene.Children) VisitScene(child);
        }
        if (loadedScene != null) VisitScene(loadedScene);
        var owners = new HashSet<EntityHierarchyDocument>();
        if (document != null) owners.Add(document);
        if (PresentedDocument != null) owners.Add(PresentedDocument);
        foreach (var child in childScenes.Values) owners.Add(child.Document);
        foreach (var owner in owners) foreach (var field in owner.ObservedComponentFields)
        {
            if (field.Type != typeof(Skybox) || !field.IsContentReference || !field.Node.MemberDescriptor.HasSet) continue;
            var entity = ResolveRuntimeEntity(owner, field.Component.Entity.Id);
            var component = entity == null ? null : FindObservedRuntimeComponent(entity, field.Component.Id);
            if (component == null || field.ReadRuntime(component) is not Skybox previous || !Matches(previous)) continue;
            changes.Add((() => field.WriteRuntime(component, replacement), () => field.WriteRuntime(component, previous)));
        }
        var applied = 0;
        try { while (applied < changes.Count) changes[applied++].Apply(); }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            while (applied > 0) try { changes[--applied].Undo(); } catch (Exception recovery) { errors.Add(recovery); }
            if (errors.Count > 1) throw new AggregateException("The skybox bindings could not be restored.", errors);
            throw;
        }
        var key = (typeof(Skybox), url);
        var changed = changes.Count != 0 || !componentContent.TryGetValue(key, out var cached) || !ReferenceEquals(cached, replacement);
        if (componentContent.TryGetValue(key, out var old) && !ReferenceEquals(old, replacement)
            && !borrowedComponentContent.Contains(key)) content?.Unload(old);
        componentContent[key] = replacement;
        borrowedComponentContent.Add(key);
        if (changed) NotifyRuntimeContentInstalled();
    }
}
