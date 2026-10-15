using Stride.Core.Assets;
using Stride.Core.Serialization;
using Stride.Engine;
using Stride.Rendering;

namespace Stride.Editor.ImGui;

internal sealed partial class EngineViewport
{
    private readonly HashSet<(Type Type, string Url)> borrowedComponentContent = [];
    private void CacheRefreshedRuntimeModel(string url, Model model)
    {
        var key = (typeof(Model), url);
        if (componentContent.TryGetValue(key, out var previous) && !ReferenceEquals(previous, model)
            && !borrowedComponentContent.Contains(key)) content?.Unload(previous);
        componentContent[key] = model;
        borrowedComponentContent.Add(key);
        ApplyRuntimeTextureOverrides(model);
    }
    private object? ResolveRuntimeContentReference(object? value)
    {
        if (value == null || value is AssetReference or UrlReferenceBase) return value;
        var reference = AttachedReferenceManager.GetAttachedReference(value);
        if (reference == null) throw new InvalidOperationException("The property does not contain an asset reference.");
        var key = (Type: value.GetType(), Url: reference.Url);
        if (key.Type == typeof(Stride.Rendering.Skyboxes.Skybox) && game is EditorGame editor
            && editor.TryGetRefreshedSkybox(key.Url, out var refreshedSkybox)) return refreshedSkybox;
        if (!componentContent.TryGetValue(key, out var loaded))
        {
            loaded = content!.Load(key.Type, ResolveRuntimeComponentContentUrl(reference.Id, key.Url));
            componentContent.Add(key, loaded);
        }
        ApplyRuntimeTextureOverrides(loaded);
        return loaded;
    }

    private string ResolveRuntimeComponentContentUrl(AssetId id, string url)
    {
        if (url.StartsWith("/", StringComparison.Ordinal)) return url;
        if (game is not EditorGame editor) throw new InvalidOperationException("Runtime content resolution requires the owning editor package session.");
        return editor.ResolveComponentContentUrl(id);
    }

    private void NotifyRuntimeContentReferenceChanged(ComponentField field, EntityComponent component, object? value)
    {
        ApplyRuntimeTextureOverrides(component);
        if (component is not ModelComponent model || field.Name != nameof(ModelComponent.Model)) return;
        selectionRuntimeVersion++;
        var reference = ContentAssetReference.Read(field.Node.Retrieve());
        if (reference == null) modelUrls.Remove(model); else modelUrls[model] = reference.Location;
        if (model.Entity != null) Picker?.CacheEntity(model.Entity);
    }
}

internal sealed partial class EditorGame
{
    internal string ResolveComponentContentUrl(AssetId id)
    {
        var item = workspace?.FindAsset(id) ?? throw new InvalidOperationException("Runtime component content has no active package session.");
        return NativeAssetUrls.Qualify(item);
    }
}
