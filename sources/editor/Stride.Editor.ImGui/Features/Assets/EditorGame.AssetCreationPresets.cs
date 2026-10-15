using Stride.Assets;
using Stride.Assets.Models;
using Stride.Physics.Assets;
using Stride.Assets.Skyboxes;
using Stride.Core.Assets;
using Stride.Core.Serialization;
using Stride.Graphics;
using Stride.Physics;
using Stride.Rendering;
using Stride.Rendering.ProceduralModels;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private Asset CreateAssetTemplateValue(Package package, AssetCreationPanel.Choice choice, AssetId? reference)
    {
        var compositor = choice.Template?.Id switch
        {
            var id when id == Guid.Parse("20947F4A-7B50-4716-AC85-D10EFF58CD33") => StridePackageUpgrader.DefaultGraphicsCompositorLevel9Url,
            var id when id == Guid.Parse("D4EE3BD3-9B06-460E-9175-D6AFB2459463") => StridePackageUpgrader.DefaultGraphicsCompositorLevel10Url,
            var id when id == Guid.Parse("4BC182D7-69D5-4BE2-9AF3-1C82F67B629D") => "/Stride.Voxels/GraphicsCompositor/DefaultGraphicsCompositorVoxels",
            _ => null,
        };
        var parent = compositor == null ? null : package.FindAsset(compositor);
        if (parent != null && authoring?.Find(parent.Id)?.Document is IEditorAssetGraphDocument baseGraph) _ = baseGraph.AuthoringGraph;
        var asset = parent != null ? CanonicalTemplateSnapshot(parent).CreateDerivedAsset() : choice.Factory.New();
        var kind = AssetCreationPanel.AssetCreationReferenceKind(choice.Template?.Id);
        if (kind == null) return asset;
        var item = reference == null ? null : workspace!.Session.FindAsset(reference.Value)
            ?? throw new InvalidOperationException("The selected template asset is no longer available.");
        if (item != null && !kind.Value.Types.Any(type => type.IsInstanceOfType(item.Asset))) throw new InvalidOperationException("The selected asset type is not accepted by this template.");
        T? Content<T>() where T : class, new() => item == null ? null : AttachedReferenceManager.CreateProxyObject<T>(item.Id, item.Location);
        switch (asset)
        {
            case SkyboxAsset skybox: skybox.CubeMap = Content<Texture>(); break;
            case ProceduralModelAsset { Type: PrimitiveProceduralModelBase primitive }: primitive.MaterialInstance.Material = Content<Material>(); break;
            case ColliderShapeAsset collider when collider.ColliderShapes.FirstOrDefault() is StaticMeshColliderShapeDesc mesh: mesh.Model = Content<Model>(); break;
            case ColliderShapeAsset collider when collider.ColliderShapes.FirstOrDefault() is ConvexHullColliderShapeDesc hull: hull.Model = Content<Model>(); break;
            default:
                if (choice.Template?.Id == Guid.Parse("FDBCB65A-9866-4EF2-9A03-AF21F943BDFD"))
                    (asset.GetType().GetField("Model") ?? throw new InvalidOperationException("The hull template has no native model field.")).SetValue(asset, Content<Model>());
                else throw new InvalidOperationException("This specialized template has no supported native reference target.");
                break;
        }
        return asset;
    }
}
