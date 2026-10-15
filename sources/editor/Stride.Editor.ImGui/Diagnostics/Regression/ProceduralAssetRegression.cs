using Stride.Assets.Models;
using Stride.Core.Assets;
using Stride.Core.Mathematics;
using Stride.Core.Serialization;
using Stride.Rendering.ProceduralModels;

namespace Stride.Editor.ImGui;

internal static class ProceduralAssetRegression
{
    public static void Run(ProjectWorkspace workspace)
    {
        var spherePath = workspace.Assets.Single(entry => entry.Name == "Sphere" && entry.Extension == ".sdpromodel").Path;
        using var sphere = new ProceduralAssetDocument(spherePath);
        var sphereType = (SphereProceduralModel)sphere.Asset.Type;
        var id = sphere.Asset.Id;
        var material = AttachedReferenceManager.GetAttachedReference(sphereType.MaterialInstance.Material)!;
        var radius = sphere.Fields.Single(field => field.Name == nameof(SphereProceduralModel.Radius));
        Require(sphere.CanEdit && radius.Node is Stride.Core.Assets.Quantum.IAssetMemberNode, "Asset editing must use the real asset graph/member nodes");
        sphere.BeginEdit(radius); sphere.EndEdit(); Require(!sphere.IsDirty && !sphere.CanUndo, "No-op asset edit must retain clean history");
        sphere.Update(radius, 0.75f); sphere.Update(radius, 1.25f);
        Require(sphere.IsDirty && sphere.CanUndo && sphereType.Radius == 1.25f, "Active asset edits must expose authored state and dirty history");
        sphere.EndEdit(); sphere.Undo(); Require(sphereType.Radius == 0.5f && !sphere.IsDirty && !sphere.CanUndo, "Continuous asset editing must undo in one operation");
        sphere.Redo(); sphere.Save();
        using (var reopened = new ProceduralAssetDocument(spherePath))
        {
            var type = (SphereProceduralModel)reopened.Asset.Type;
            var reference = AttachedReferenceManager.GetAttachedReference(type.MaterialInstance.Material)!;
            Require(type.Radius == 1.25f && reopened.Asset.Id == id && reference.Id == material.Id && reference.Url == material.Url,
                "Asset save must retain procedural values, asset identity and actual material dependency references");
        }
        sphere.Undo(); Require(sphere.IsDirty, "Undo away from the asset save point must be dirty");
        sphere.Redo(); Require(!sphere.IsDirty, "Redo to the asset save point must be clean");
        sphere.Undo(); sphere.Save();
        var materialField = sphere.Fields.Single(field => field.Name == "Material");
        var shadows = sphere.Fields.Single(field => field.Name == "IsShadowCaster");
        var originalMaterial = sphereType.MaterialInstance.Material;
        var alternate = workspace.Package.Assets.Single(item => item.UnqualifiedUrl.ToString() == "Ground Material");
        sphere.Update(materialField, AttachedReferenceManager.CreateProxyObject<Stride.Rendering.Material>(alternate.Id, alternate.Location.ToString())); sphere.EndEdit();
        sphere.Update(shadows, false); sphere.EndEdit(); sphere.Save();
        using (var reopenedMaterial = new ProceduralAssetDocument(spherePath))
        {
            var instance = ((SphereProceduralModel)reopenedMaterial.Asset.Type).MaterialInstance;
            var reference = AttachedReferenceManager.GetAttachedReference(instance.Material)!;
            Require(reference.Id == alternate.Id && reference.Url == alternate.Location.ToString() && !instance.IsShadowCaster,
                "Nested material edits must retain dependency ID/URL and shadow setting through actual asset YAML");
        }
        sphere.Undo(); Require(sphereType.MaterialInstance.IsShadowCaster, "Shadow undo must preserve the independently edited material");
        sphere.Undo(); Require(ReferenceEquals(sphereType.MaterialInstance.Material, originalMaterial), "Reference undo must retain the original authored proxy");
        sphere.Save();
        sphere.Update(materialField, null); sphere.EndEdit(); sphere.Undo();
        Require(ReferenceEquals(sphereType.MaterialInstance.Material, originalMaterial) && !sphere.IsDirty, "Clearing and undoing a material must preserve save point");
        sphere.Update(radius, 2f); sphere.CancelEdit(); Require(sphereType.Radius == 0.5f && !sphere.IsDirty, "Asset Cancel must retain original values/save state");
        try { sphere.Update(radius, float.NaN); throw new InvalidOperationException("NaN asset edits must be rejected"); }
        catch (ArgumentException) { }
        Require(sphereType.Radius == 0.5f && !sphere.IsDirty, "Rejected asset values must retain clean authored data");

        var planePath = workspace.Assets.Single(entry => entry.Name == "Ground" && entry.Extension == ".sdpromodel").Path;
        using var plane = new ProceduralAssetDocument(planePath);
        var size = plane.Fields.Single(field => field.Name == nameof(PlaneProceduralModel.Size));
        var tessellation = plane.Fields.Single(field => field.Name == nameof(PlaneProceduralModel.Tessellation));
        plane.Update(size, new Vector2(12.5f, 7.25f)); plane.EndEdit();
        plane.Update(tessellation, new Int2(3, 4)); plane.EndEdit(); plane.Save();
        var savedPlane = (PlaneProceduralModel)AssetFileSerializer.Load<ProceduralModelAsset>(planePath).Asset.Type;
        Require(savedPlane.Size == new Vector2(12.5f, 7.25f) && savedPlane.Tessellation == new Int2(3, 4), "Float/integer asset vectors must save through asset graph metadata");
        plane.Undo(); plane.Undo(); plane.Save();
        Require(((PlaneProceduralModel)plane.Asset.Type).Size == new Vector2(10) && ((PlaneProceduralModel)plane.Asset.Type).Tessellation == new Int2(1), "Asset regression must restore its original fixture");
        var originalPlane = plane.Asset.Type;
        plane.ChangeType(typeof(PlaneProceduralModel));
        Require(ReferenceEquals(plane.Asset.Type, originalPlane) && !plane.IsDirty, "Selecting the same model type must preserve authored object and clean save point");
        try { plane.ChangeType(typeof(string)); throw new Exception("Invalid model type should be rejected"); } catch (InvalidOperationException) { }
        Require(ReferenceEquals(plane.Asset.Type, originalPlane) && !plane.IsDirty, "Rejected type changes must preserve authored data/history");
        var originalSize = plane.Fields.Single(field => field.Name == "Size");
        plane.Update(originalSize, new Vector2(13, 9)); plane.EndEdit();
        plane.ChangeType(typeof(CubeProceduralModel));
        Require(plane.Asset.Type is CubeProceduralModel && plane.Fields.Single(field => field.Name == "Size").Type == typeof(Vector3),
            "Type replacement must rebuild actual graph fields for the new concrete model");
        try { plane.Update(originalSize, new Vector2(99)); throw new Exception("Stale type fields must be rejected"); } catch (InvalidOperationException) { }
        var cubeSize = plane.Fields.Single(field => field.Name == "Size");
        plane.Update(cubeSize, new Vector3(2, 3, 4)); plane.EndEdit(); plane.Save();
        Require(AssetFileSerializer.Load<ProceduralModelAsset>(planePath).Asset.Type is CubeProceduralModel cube && cube.Size == new Vector3(2, 3, 4),
            "Type and new fields must serialize through the same asset graph");
        plane.Undo(); plane.Undo();
        Require(ReferenceEquals(plane.Asset.Type, originalPlane) && ((PlaneProceduralModel)plane.Asset.Type).Size == new Vector2(13, 9),
            "Type undo must restore the original authored object and its previous edits");
        plane.Undo(); Require(((PlaneProceduralModel)plane.Asset.Type).Size == new Vector2(10), "Pre-replacement member history must still target the restored graph object");
        plane.Redo(); plane.Redo(); plane.Redo();
        Require(((CubeProceduralModel)plane.Asset.Type).Size == new Vector3(2, 3, 4) && !plane.IsDirty, "Redo through type replacement must reach saved new shape and fields");
        plane.Undo(); plane.Undo(); plane.Undo(); plane.Save();
        foreach (var modelType in plane.ModelTypes)
        {
            plane.ChangeType(modelType); plane.Save();
            Require(AssetFileSerializer.Load<ProceduralModelAsset>(planePath).Asset.Type.GetType() == modelType, "Every offered procedural type must serialize/reopen");
            if (modelType != typeof(PlaneProceduralModel)) plane.Undo();
        }
        plane.Save();
        var inheritedPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(spherePath)!, "Inherited sphere.sdpromodel");
        var inherited = new ProceduralModelAsset { Type = new SphereProceduralModel(), Archetype = new AssetReference(id, "Sphere") };
        AssetFileSerializer.Save(inheritedPath, inherited, null);
        var inheritedBytes = File.ReadAllBytes(inheritedPath);
        using (var inheritedDocument = new ProceduralAssetDocument(inheritedPath))
        {
            Require(!inheritedDocument.CanEdit, "Inherited asset editing must remain restricted until override semantics are supported");
            try { inheritedDocument.Update(inheritedDocument.Fields.Single(field => field.Name == nameof(SphereProceduralModel.Radius)), 2f); throw new Exception("Inherited edit should be rejected"); }
            catch (InvalidOperationException) { }
            try { inheritedDocument.Save(); throw new Exception("Inherited save should be rejected"); }
            catch (InvalidOperationException) { }
        }
        Require(File.ReadAllBytes(inheritedPath).SequenceEqual(inheritedBytes), "Unsupported inherited assets must not be flattened or rewritten");
        var external = File.ReadAllBytes(spherePath).Concat(System.Text.Encoding.UTF8.GetBytes("\n# External asset edit\n")).ToArray();
        File.WriteAllBytes(spherePath, external);
        try { sphere.Save(); throw new InvalidOperationException("External asset changes must block save"); }
        catch (IOException) { }
        Require(File.ReadAllBytes(spherePath).SequenceEqual(external), "Asset save conflict must not clobber external changes");
        Console.WriteLine("PROCEDURAL ASSET REGRESSION PASS: real AssetPropertyGraph, grouped history/save points/Cancel, finite validation, scalar/vector YAML values, material references/asset identity, inherited guards and external edit protection.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
