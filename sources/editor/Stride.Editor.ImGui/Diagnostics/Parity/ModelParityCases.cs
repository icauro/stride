using Stride.Assets.Models;
using Stride.Core.Assets;
using Stride.Core.Mathematics;
using Stride.Rendering.ProceduralModels;

namespace Stride.Editor.ImGui;

internal sealed record ModelParityCase(string Id, Type ModelType, string Property, object Value);
internal sealed record ModelParityCaseResult(string Id, string ModelType, string Property, string AssetPath, string AssetId,
    bool HistoryVerified, bool PersistenceVerified, string ValueType, string BaselineValue, string ExpectedValue, string ReopenedValue);

/// <summary>One disposable batch exercises actual native Quantum/history/YAML paths for stock primitive properties.</summary>
internal static class ModelParityCases
{
    internal static IReadOnlyList<ModelParityCase> All
    {
        get
        {
            var cases = new List<ModelParityCase>
            {
                new("M29", typeof(SphereProceduralModel), "Tessellation", 12),
                new("M30", typeof(CapsuleProceduralModel), "Length", 1.25f),
                new("M31", typeof(CapsuleProceduralModel), "Radius", .5f),
                new("M32", typeof(CapsuleProceduralModel), "Tessellation", 12),
                new("M33", typeof(ConeProceduralModel), "Height", 1.75f),
                new("M34", typeof(ConeProceduralModel), "Radius", .75f),
                new("M35", typeof(ConeProceduralModel), "Tessellation", 12),
                new("M36", typeof(CylinderProceduralModel), "Height", 1.25f),
                new("M37", typeof(CylinderProceduralModel), "Radius", .75f),
                new("M38", typeof(CylinderProceduralModel), "Tessellation", 12),
                new("M39", typeof(GeoSphereProceduralModel), "Radius", 1.25f),
                new("M40", typeof(GeoSphereProceduralModel), "Tessellation", 2),
                new("M41", typeof(TeapotProceduralModel), "Size", 1.5f),
                new("M42", typeof(TeapotProceduralModel), "Tessellation", 4),
                new("M43", typeof(TorusProceduralModel), "Radius", .75f),
                new("M44", typeof(TorusProceduralModel), "Thickness", .25f),
                new("M45", typeof(TorusProceduralModel), "Tessellation", 12),
            };
            foreach (var type in new[] { typeof(SphereProceduralModel), typeof(CapsuleProceduralModel), typeof(ConeProceduralModel),
                         typeof(CylinderProceduralModel), typeof(GeoSphereProceduralModel), typeof(TeapotProceduralModel),
                         typeof(TorusProceduralModel), typeof(PlaneProceduralModel), typeof(CubeProceduralModel) })
            {
                cases.Add(new("M46:" + type.Name, type, "UvScale", new Vector2(2, 3)));
                cases.Add(new("M47:" + type.Name, type, "LocalOffset", new Vector3(.25f, -.5f, .75f)));
                cases.Add(new("M48:" + type.Name, type, "NumberOfTextureCoordinates", 3));
            }
            return cases;
        }
    }
    internal static ModelParityCaseResult[] RunHistory(string privateRoot)
    {
        privateRoot = Path.GetFullPath(privateRoot);
        if (!Path.GetFileName(privateRoot).StartsWith("model-parity-", StringComparison.Ordinal))
            throw new InvalidOperationException("Model parity output requires an explicitly owned model-parity-* disposable directory.");
        if (Directory.Exists(privateRoot) || File.Exists(privateRoot))
            throw new IOException("Model parity must create its own new disposable directory.");
        Directory.CreateDirectory(privateRoot);
        var results = new List<ModelParityCaseResult>();
        foreach (var test in All)
        {
            var asset = new ProceduralModelAsset { Type = (IProceduralModel)Activator.CreateInstance(test.ModelType)! };
            var property = test.ModelType.GetProperty(test.Property) ?? throw new InvalidOperationException("Missing stock primitive property: " + test.Property);
            var baseline = property.GetValue(asset.Type);
            var path = Path.Combine(privateRoot, test.Id.Replace(':', '-') + ".sdpromodel");
            AssetFileSerializer.Save(path, asset, null);
            using var document = new ProceduralAssetDocument(path);
            var field = document.Fields.Single(row => row.Name == test.Property);
            document.Update(field, test.Value); document.EndEdit();
            Require(Equals(property.GetValue(document.Asset.Type), test.Value) && document.IsDirty && document.CanUndo, test.Id + " actual graph edit/history");
            document.Undo();
            Require(Equals(property.GetValue(document.Asset.Type), baseline) && !document.IsDirty && !document.CanUndo, test.Id + " grouped undo/save-point");
            document.Redo(); document.Save();
            Require(!document.IsDirty && document.CanUndo, test.Id + " redo/save-point");
            var reopened = AssetFileSerializer.Load<ProceduralModelAsset>(path).Asset;
            Require(reopened.Id == asset.Id && reopened.Type.GetType() == test.ModelType
                && Equals(property.GetValue(reopened.Type), test.Value), test.Id + " persisted identity/value/type");
            document.Undo(); Require(document.IsDirty, test.Id + " undo from saved state");
            document.Redo(); Require(!document.IsDirty, test.Id + " redo to saved state");
            results.Add(new(test.Id, test.ModelType.FullName!, test.Property, path, asset.Id.ToString(), true, true,
                test.Value.GetType().FullName!, Format(baseline), Format(test.Value), Format(property.GetValue(reopened.Type))));
        }
        return results.ToArray();
    }
    private static string Format(object? value) => value switch
    {
        float scalar => scalar.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        int integer => integer.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Vector2 vector => "[" + Format(vector.X) + "," + Format(vector.Y) + "]",
        Vector3 vector => "[" + Format(vector.X) + "," + Format(vector.Y) + "," + Format(vector.Z) + "]",
        _ => value?.ToString() ?? "null",
    };
    private static void Require(bool condition, string operation)
    {
        if (!condition) throw new InvalidOperationException("Model parity failed: " + operation);
    }
}
