using Stride.Graphics;
using Stride.Engine;
using Stride.Rendering;

namespace Stride.Editor.ImGui;

internal sealed partial class EngineViewport
{
    private long runtimeTextureGeneration;
    private readonly Dictionary<object, long> runtimeTextureContentGenerations = new(ReferenceEqualityComparer.Instance);
    private void ApplyRuntimeTextureOverrides(object loaded)
    {
        if (runtimeTextureGeneration == 0 || !CompiledTextureConsumers.SupportsContent(loaded)
            || loaded is not EntityComponent && runtimeTextureContentGenerations.GetValueOrDefault(loaded) == runtimeTextureGeneration) return;
        foreach (var pair in componentContent)
        {
            if (!borrowedComponentContent.Contains(pair.Key)) continue;
            if (pair.Value is Texture texture) CompiledTextureConsumers.ReplaceContent(loaded, pair.Key.Url, texture);
            else if (pair.Value is SpriteSheet sheet)
                CompiledTextureConsumers.Apply(CompiledSpriteSheetConsumers.Prepare(loaded, pair.Key.Url, sheet));
        }
        // Components are replaced independently of loaded content; avoid retaining removed owners.
        if (loaded is not EntityComponent) runtimeTextureContentGenerations[loaded] = runtimeTextureGeneration;
    }
    private void ClearRuntimeTextureOverrides() { runtimeTextureGeneration = 0; runtimeTextureContentGenerations.Clear(); }
    internal void ReplaceRuntimeTexture(string url, Texture texture, EntityHierarchyDocument? document)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var changes = CompiledTextureConsumers.Prepare(loadedScene, url, texture, visited);
        // Cached pages/sheets may be detached from the current scene but reattached later.
        // Replace their borrowed texture before the previous import owner can retire.
        foreach (var loaded in componentContent.Values)
            if (CompiledTextureConsumers.SupportsContent(loaded))
                CompiledTextureConsumers.PrepareAdditionalConsumer(loaded, url, texture, changes, visited);
        if (document != null) foreach (var field in document.ObservedComponentFields)
        {
            if (field.Type != typeof(Texture) || !field.IsContentReference || !runtimeEntities.TryGetValue(field.Component.Entity.Id, out var entity)) continue;
            var component = entity.Components.FirstOrDefault(candidate => candidate.Id == field.Component.Id);
            if (component != null && field.Node.MemberDescriptor.HasSet && field.ReadRuntime(component) is Texture current
                && CompiledTextureConsumers.Matches(current, url))
                changes.Add(new(() => field.WriteRuntime(component, texture), () => field.WriteRuntime(component, current)));
        }
        CompiledTextureConsumers.Apply(changes);
        var key = (typeof(Texture), url);
        if (componentContent.TryGetValue(key, out var previous) && !ReferenceEquals(previous, texture)
            && !borrowedComponentContent.Contains(key)) content?.Unload(previous);
        componentContent[key] = texture; borrowedComponentContent.Add(key);
        runtimeTextureGeneration++;
    }

    internal void ReplaceRuntimeSpriteSheet(string url, SpriteSheet sheet, EntityHierarchyDocument? document)
    {
        // The caller keeps the new compiled-content owner alive while these references exist.
        ApplyRuntimeTextureOverrides(sheet);
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var changes = CompiledSpriteSheetConsumers.Prepare(loadedScene, url, sheet, visited);
        foreach (var loaded in componentContent.Values)
            CompiledSpriteSheetConsumers.Prepare(loaded, url, sheet, visited, changes);
        if (document != null) foreach (var field in document.ObservedComponentFields)
        {
            if (field.Type != typeof(SpriteSheet) || !field.IsContentReference
                || !runtimeEntities.TryGetValue(field.Component.Entity.Id, out var entity)) continue;
            var component = entity.Components.FirstOrDefault(candidate => candidate.Id == field.Component.Id);
            if (component != null && field.Node.MemberDescriptor.HasSet && field.ReadRuntime(component) is SpriteSheet previousSheet
                && string.Equals(Stride.Core.Serialization.AttachedReferenceManager.GetUrl(previousSheet), url, StringComparison.Ordinal)
                && !ReferenceEquals(previousSheet, sheet))
                changes.Add(new(() => field.WriteRuntime(component, sheet), () => field.WriteRuntime(component, previousSheet)));
        }
        CompiledTextureConsumers.Apply(changes);
        var key = (typeof(SpriteSheet), url);
        if (componentContent.TryGetValue(key, out var previous) && !ReferenceEquals(previous, sheet))
        {
            runtimeTextureContentGenerations.Remove(previous);
            if (!borrowedComponentContent.Contains(key)) content?.Unload(previous);
        }
        componentContent[key] = sheet; borrowedComponentContent.Add(key);
        runtimeTextureGeneration++;
    }
}
