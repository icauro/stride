using Stride.Core.Serialization;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;

namespace Stride.Editor.ImGui;

/// <summary>Updates actual runtime texture bindings without mutating authored asset references.</summary>
internal static partial class CompiledTextureConsumers
{
    internal readonly record struct Change(Action Apply, Action Undo);
    public static bool Matches(Texture? texture, string url) => texture != null
        && string.Equals(AttachedReferenceManager.GetUrl(texture), url, StringComparison.Ordinal);
    public static void Replace(Scene? scene, string url, Texture texture)
    {
        Apply(Prepare(scene, url, texture));
    }
    internal static List<Change> Prepare(Scene? scene, string url, Texture texture, HashSet<object>? consumers = null)
    {
        var changes = new List<Change>();
        if (scene == null) return changes;
        consumers ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Visit(Entity entity)
        {
            foreach (var component in entity.Components)
                PrepareAdditionalConsumer(component, url, texture, changes, consumers);
            foreach (var child in entity.Transform.Children) Visit(child.Entity);
        }
        void VisitScene(Scene current)
        {
            foreach (var entity in current.Entities) Visit(entity);
            foreach (var child in current.Children) VisitScene(child);
        }
        VisitScene(scene);
        return changes;
    }
    public static void Replace(Material material, string url, Texture texture)
    {
        var changes = new List<Change>(); PrepareMaterial(material, url, texture, changes); Apply(changes);
    }
    private static void PrepareMaterial(Material material, string url, Texture texture, List<Change> changes)
    {
        foreach (var pass in material.Passes)
            PrepareParameters(pass.Parameters, url, texture, changes);
    }
    private static void PrepareParameters(ParameterCollection parameters, string url, Texture texture, List<Change> changes)
    {
        if (parameters.ObjectValues == null) return;
        foreach (var key in parameters.ParameterKeyInfos)
            if (key.IsResourceParameter && key.BindingSlot >= 0 && key.BindingSlot < parameters.ObjectValues.Length
                && parameters.ObjectValues[key.BindingSlot] is Texture previous && Matches(previous, url))
                changes.Add(new(() => parameters.SetObject(key.Key, texture), () => parameters.SetObject(key.Key, previous)));
    }
    internal static void Apply(IReadOnlyList<Change> changes)
    {
        var applied = 0;
        try { while (applied < changes.Count) changes[applied++].Apply(); }
        catch (Exception error)
        {
            var failures = new List<Exception> { error };
            while (applied > 0) try { changes[--applied].Undo(); } catch (Exception rollback) { failures.Add(rollback); }
            if (failures.Count > 1) throw new AggregateException("Texture consumer update could not be fully restored.", failures);
            throw;
        }
    }
}
