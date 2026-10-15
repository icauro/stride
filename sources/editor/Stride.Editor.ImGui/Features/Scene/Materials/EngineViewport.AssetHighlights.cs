using Stride.Assets.Materials;
using Stride.Assets.Models;
using Stride.Assets.Textures;
using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;
using Stride.Core.Mathematics;
using Stride.Core.Serialization;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Rendering;

namespace Stride.Editor.ImGui;

internal sealed partial class EngineViewport
{
    private readonly Dictionary<Material, Color4> selectedAssetMaterials = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Mesh> selectedAssetMeshes = new(ReferenceEqualityComparer.Instance);
    private double selectedAssetHighlightStart;
    private static readonly Color4 DirectAssetColor = new(1f, .35f, .25f, .8f);
    private static readonly Color4 IndirectAssetColor = new(1f, .65f, .60f, .8f);
    internal void HighlightSelectedAssets(IReadOnlyList<AssetItem> assets, IReadOnlyDictionary<AssetId, AssetDependencies> dependencies)
    {
        ClearSelectedAssetHighlights();
        var materials = new Dictionary<string, Color4>(StringComparer.Ordinal);
        var models = new HashSet<string>(StringComparer.Ordinal);
        void AddMaterial(AssetItem item, Color4 color)
        {
            if (item.Asset is not MaterialAsset) return;
            var url = item.Location.ToString();
            if (!materials.ContainsKey(url) || color == DirectAssetColor) materials[url] = color;
        }
        IEnumerable<AssetItem> Incoming(AssetId source, bool recursive)
        {
            var visited = new HashSet<AssetId> { source }; var queue = new Queue<AssetId>(); queue.Enqueue(source);
            while (queue.TryDequeue(out var id))
                if (dependencies.TryGetValue(id, out var snapshot))
                    foreach (var link in snapshot.LinksIn)
                        if ((link.Type & ContentLinkType.Reference) != 0 && visited.Add(link.Item.Id))
                        { yield return link.Item; if (recursive) queue.Enqueue(link.Item.Id); }
        }
        foreach (var asset in assets)
        {
            if (asset.Asset is ModelAsset or ProceduralModelAsset) models.Add(asset.Location.ToString());
            else if (asset.Asset is MaterialAsset)
            {
                AddMaterial(asset, DirectAssetColor);
                foreach (var item in Incoming(asset.Id, true)) AddMaterial(item, IndirectAssetColor);
            }
            else if (asset.Asset is TextureAsset)
                foreach (var material in Incoming(asset.Id, false).Where(item => item.Asset is MaterialAsset))
                {
                    AddMaterial(material, DirectAssetColor);
                    foreach (var item in Incoming(material.Id, true)) AddMaterial(item, IndirectAssetColor);
                }
        }
        string? Url(object resource)
        {
            var attached = AttachedReferenceManager.GetUrl(resource);
            if (!string.IsNullOrEmpty(attached)) return attached;
            if (content != null && content.TryGetAssetUrl(resource, out var url)) return url;
            return null;
        }
        void Visit(Entity entity)
        {
            foreach (var component in entity.Components.OfType<ModelComponent>())
            {
                if (component.Model is not { } model) continue;
                var modelUrl = modelUrls.GetValueOrDefault(component) ?? Url(model);
                if (modelUrl != null && models.Contains(modelUrl))
                    foreach (var mesh in model.Meshes) selectedAssetMeshes.Add(mesh);
                foreach (var mesh in model.Meshes)
                    if (component.GetMaterial(mesh.MaterialIndex) is { } material && Url(material) is { } url && materials.TryGetValue(url, out var color))
                        selectedAssetMaterials[material] = color;
            }
        }
        foreach (var entity in runtimeEntities.Values) if (entity.Scene == loadedScene) Visit(entity);
        foreach (var child in childScenes.Values)
            foreach (var entity in child.Entities.Values) if (entity.Scene == child.Scene) Visit(entity);
        selectedAssetHighlightStart = game.UpdateTime.Total.TotalSeconds;
        UpdateSelectedAssetHighlights();
    }
    internal void UpdateSelectedAssetHighlights()
    {
        if (selectedAssetMaterials.Count == 0 && selectedAssetMeshes.Count == 0) return;
        var ratio = 1f - (float)(game.UpdateTime.Total.TotalSeconds - selectedAssetHighlightStart);
        if (ratio <= 0) { ClearSelectedAssetHighlights(); return; }
        foreach (var pair in selectedAssetMaterials)
            materialHighlight.MaterialHighlightColors[pair.Key] = Color4.PremultiplyAlpha(new Color4(pair.Value.R, pair.Value.G, pair.Value.B, pair.Value.A * ratio));
        var meshColor = Color4.PremultiplyAlpha(new Color4(DirectAssetColor.R, DirectAssetColor.G, DirectAssetColor.B, DirectAssetColor.A * ratio));
        foreach (var mesh in selectedAssetMeshes) materialHighlight.MeshHighlightColors[mesh] = meshColor;
    }
    private void ClearSelectedAssetHighlights()
    {
        foreach (var material in selectedAssetMaterials.Keys) materialHighlight.MaterialHighlightColors.Remove(material);
        foreach (var mesh in selectedAssetMeshes) materialHighlight.MeshHighlightColors.Remove(mesh);
        selectedAssetMaterials.Clear(); selectedAssetMeshes.Clear();
    }
}
