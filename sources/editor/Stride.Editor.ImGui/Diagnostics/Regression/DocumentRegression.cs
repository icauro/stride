using Stride.Core.Mathematics;

namespace Stride.Editor.ImGui;

internal static class DocumentRegression
{
    public static int Run(string packagePath)
    {
        PickingRequestRegression.Run();
        CameraProjectionRegression.Run();
        CameraSettingsRegression.Run();
        var copy = UiWorkflowTest.CopyFixture(packagePath);
        var workspace = new ProjectWorkspace(copy);
        ProceduralAssetRegression.Run(workspace);
        var scene = workspace.Assets.First(asset => asset.Extension == ".sdscene").Path;
        HierarchyCreationRegression.Run(scene);
        HierarchyDeletionRegression.Run(scene);
        HierarchyReparentingRegression.Run(scene);
        HierarchyOrderingRegression.Run(scene);
        var document = new SceneDocument(scene);
        document.Selected = document.Asset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Sphere").Entity;
        var entity = document.Selected;
        var original = TransformState.Read(entity);
        var identities = document.Asset.Hierarchy.Parts.Keys.ToArray();
        Require(document.CanEditSelected, "Fixture dependencies must resolve");
        document.BeginTransformEdit(); document.EndTransformEdit();
        Require(!document.CanUndo && !document.IsDirty, "No-op editing must not create a transaction");
        document.Rename(entity, entity.Name);
        Require(!document.CanUndo && !document.IsDirty, "No-op rename must not create a transaction");
        var components = entity.Components.ToArray();
        document.Rename(entity, "Ground");
        Require(document.Asset.Hierarchy.Parts.Values.Count(part => part.Entity.Name == "Ground") == 2, "Duplicate entity names must remain legal");
        document.Undo();
        Require(entity.Name == "Sphere" && !document.IsDirty, "Rename undo must restore the original clean state");
        document.Rename(entity, "Renamed – 星 %##"); document.Save();
        Require(new SceneDocument(scene).Asset.Hierarchy.Parts[entity.Id].Entity.Name == "Renamed – 星 %##", "Unicode and literal label markers must round-trip in names");
        Require(ReferenceEquals(document.Asset.Hierarchy.Parts[entity.Id].Entity, entity) && components.SequenceEqual(entity.Components), "Rename must preserve entity identity and component references");
        document.Undo(); document.Redo();
        Require(entity.Name == "Renamed – 星 %##" && !document.IsDirty, "Rename redo must restore its save point");
        document.Undo(); document.Save();
        VerifyVectors(document, scene, entity);
        VerifyProjectVectorTypes(document, scene, entity);
        VerifyNumericRanges(document, scene, entity);
        var light = document.Asset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Directional light").Entity.Get<Stride.Engine.LightComponent>()!;
        var intensity = document.GetComponentFields(light).Single(entry => entry.Name == nameof(light.Intensity));
        Require(intensity.SupportsEditing, "Quantum must expose the serializable scalar intensity field");
        document.BeginComponentEdit(intensity); document.UpdateComponent(intensity, 5f); document.UpdateComponent(intensity, 7f);
        Require(document.IsDirty && document.CanUndo, "Active component edits must expose dirty state and undo");
        document.EndComponentEdit(light);
        document.Undo(); Require(light.Intensity == 20f && !document.IsDirty, "Continuous component edits must undo as one transaction");
        document.Redo(); document.Save();
        Require(new SceneDocument(scene).Asset.Hierarchy.Parts[light.Entity.Id].Entity.Get<Stride.Engine.LightComponent>()!.Intensity == 7f, "Quantum component edit must persist without replacing component identity");
        document.Undo(); document.Save();
        try { document.UpdateComponent(intensity, float.NaN); throw new InvalidOperationException("Non-finite values must be rejected"); }
        catch (ArgumentException) { }
        Require(light.Intensity == 20f && !document.IsDirty, "Rejected values must not corrupt component state or dirty tracking");
        var nestedFields = SceneDocument.Flatten(document.GetComponentFields(light)).ToArray();
        var red = nestedFields.Single(field => field.Name == "Type.Color.Value.R");
        var color = (Stride.Rendering.Colors.ColorRgbProvider)((Stride.Rendering.Lights.LightDirectional)light.Type).Color;
        var runtimeLight = new Stride.Engine.LightComponent { Type = new Stride.Rendering.Lights.LightDirectional() };
        document.UpdateComponent(red, 0.25f); document.UpdateComponent(red, 0.5f); document.EndComponentEdit();
        Require(color.Value == new Color3(0.5f, 1f, 1f), "Nested Quantum edits must propagate boxed color values without changing sibling channels");
        red.WriteRuntime(runtimeLight, red.Node.Retrieve());
        Require(Equals(red.ReadRuntime(runtimeLight), 0.5f) && ((Stride.Rendering.Lights.LightDirectional)runtimeLight.Type).ComputeColor(Stride.Graphics.ColorSpace.Linear, 1).G == 1f,
            "Nested runtime synchronization must write back boxed structs and preserve sibling channels");
        document.Undo(); Require(color.Value == new Color3(1f) && !document.IsDirty, "Nested color edits must undo as one transaction");
        document.Redo(); document.Save();
        var savedLight = new SceneDocument(scene).Asset.Hierarchy.Parts[light.Entity.Id].Entity.Get<Stride.Engine.LightComponent>()!;
        Require(((Stride.Rendering.Colors.ColorRgbProvider)((Stride.Rendering.Lights.LightDirectional)savedLight.Type).Color).Value == color.Value,
            "Nested color edits must round-trip through the actual scene asset");
        document.Undo(); document.Save();
        var shadowEnabled = nestedFields.Single(field => field.Name == "Type.Shadow.Enabled");
        document.UpdateComponent(shadowEnabled, false); document.EndComponentEdit();
        Require(!((Stride.Rendering.Lights.LightDirectional)light.Type).Shadow.Enabled, "Nested shadow settings must edit the owned light object");
        document.Undo(); Require(((Stride.Rendering.Lights.LightDirectional)light.Type).Shadow.Enabled && !document.IsDirty, "Nested shadow undo must restore clean authored settings");
        var colorField = nestedFields.Single(field => field.Name == "Type.Color.Value");
        var hdrColor = new Color3(1.125f, 2.25f, 0.875f);
        document.UpdateComponent(colorField, hdrColor);
        Require(color.Value == hdrColor && document.IsDirty, "Whole-color edits must retain float HDR precision");
        document.CancelComponentEdit(colorField);
        Require(color.Value == new Color3(1f) && !document.IsDirty && !document.CanUndo, "Cancel must restore the initial color without creating history");
        document.UpdateComponent(colorField, hdrColor); document.EndComponentEdit(); document.Save();
        var hdrSaved = new SceneDocument(scene).Asset.Hierarchy.Parts[light.Entity.Id].Entity.Get<Stride.Engine.LightComponent>()!;
        Require(((Stride.Rendering.Colors.ColorRgbProvider)((Stride.Rendering.Lights.LightDirectional)hdrSaved.Type).Color).Value == hdrColor,
            "HDR colors must serialize without clamping or byte quantization");
        document.Undo(); document.Save();
        try { document.UpdateComponent(colorField, new Color3(float.PositiveInfinity, 1f, 1f)); throw new InvalidOperationException("Non-finite colors must be rejected"); }
        catch (ArgumentException) { }
        Require(color.Value == new Color3(1f) && !document.IsDirty, "Invalid colors must leave authored data and dirty state unchanged");
        document.BeginTransformEdit();
        entity.Transform.Position = original.Position + Vector3.UnitX;
        entity.Transform.Position = original.Position + Vector3.UnitX * 2;
        document.EndTransformEdit();
        document.Undo();
        Require(TransformState.Read(entity) == original && !document.CanUndo && !document.IsDirty, "A continuous edit must undo as one transaction");
        document.Redo(); document.Save();
        Require(!document.IsDirty, "Save point must be clean");
        document.Undo(); Require(document.IsDirty, "Undo away from save point must be dirty");
        document.Redo(); Require(!document.IsDirty, "Redo back to save point must be clean");
        document.Undo();
        document.BeginTransformEdit(); entity.Transform.Position = original.Position + Vector3.UnitX * 3; document.EndTransformEdit();
        Require(document.IsDirty && !document.CanRedo, "A branch must not reuse the old clean revision or redo history");
        document.Save();
        var reopened = new SceneDocument(scene);
        Require(identities.SequenceEqual(reopened.Asset.Hierarchy.Parts.Keys), "Entity identity must round-trip");
        Require(reopened.Asset.Hierarchy.Parts[entity.Id].Entity.Components.Count == entity.Components.Count, "Components must round-trip");
        var external = File.ReadAllBytes(scene).Concat(System.Text.Encoding.UTF8.GetBytes("\n# External edit\n")).ToArray();
        File.WriteAllBytes(scene, external);
        try { document.Save(); throw new InvalidOperationException("Save should reject an external edit"); }
        catch (IOException) { }
        Require(File.ReadAllBytes(scene).SequenceEqual(external), "Conflict detection must not overwrite external changes");
        Console.WriteLine("DOCUMENT REGRESSION PASS: Quantum component grouping/save points/validation; Vector2/Vector3/Vector4 precision/runtime/history/save, Int2/Int3/Int4 exact full-range/history/save, vector Cancel/finite validation; numeric bounds/steps/decimal rounding and parse-before-float precision; no-op edits/rename; duplicate/Unicode names; rename identity/references/save points; continuous grouping; save/undo/redo cleanliness; branching; identity/components round-trip; external edit protection. Fixture: " + scene);
        return 0;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static void VerifyVectors(SceneDocument document, string scene, Stride.Engine.Entity owner)
    {
        var camera = document.Asset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Camera").Entity;
        var script = camera.Components.Single(component => component.GetType().Name == "BasicCameraController");
        var runtime = (Stride.Engine.EntityComponent)Activator.CreateInstance(script.GetType())!;
        foreach (var name in new[] { "KeyboardMovementSpeed", "KeyboardRotationSpeed" })
        {
            var field = document.GetComponentFields(script).Single(entry => entry.Name == name);
            var original = field.Node.Retrieve();
            object edited = field.VectorDimensions == 3 ? (object)new Vector3(0.123456789f, -2.5f, 3.75f) : new Vector2(0.123456789f, -2.5f);
            Require(field.SupportsEditing && field.Children.Count == 0, "Vectors must be editable atomic properties, not duplicate scalar child trees");
            document.UpdateComponent(field, edited); document.UpdateComponent(field, edited); document.EndComponentEdit();
            field.WriteRuntime(runtime, field.Node.Retrieve());
            Require(Equals(field.ReadRuntime(runtime), edited), "Whole-vector runtime propagation must retain every axis and float precision");
            document.Undo(); Require(Equals(field.Node.Retrieve(), original) && !document.IsDirty, "Grouped vector undo must restore the clean original");
            document.Redo(); document.Save();
            var saved = new SceneDocument(scene).Asset.Hierarchy.Parts[camera.Id].Entity.Components.Single(component => component.Id == script.Id);
            Require(Equals(field.ReadRuntime(saved), edited), "Vector values and component identity must survive scene YAML round-trip");
            document.Undo(); document.Save();
            object invalid = field.VectorDimensions == 3 ? (object)new Vector3(1, float.NaN, 3) : new Vector2(float.PositiveInfinity, 2);
            try { document.UpdateComponent(field, invalid); throw new InvalidOperationException("Non-finite vector axes must be rejected"); }
            catch (ArgumentException) { }
            Require(Equals(field.Node.Retrieve(), original) && !document.IsDirty, "Rejected vectors must not alter authored values or dirty state");
        }
        // Probe the fourth axis without inventing a production engine property.
        var probe = new VectorRegressionComponent(); owner.Components.Add(probe);
        var vector4 = document.GetComponentFields(probe).Single(entry => entry.Name == nameof(probe.Value));
        var edited4 = new Vector4(0.123456789f, -2.5f, 3.75f, 4.125f);
        document.UpdateComponent(vector4, edited4);
        var runtime4 = new VectorRegressionComponent(); vector4.WriteRuntime(runtime4, vector4.Node.Retrieve());
        Require(runtime4.Value == edited4 && document.IsDirty, "Vector4 propagation must include W and preserve float precision");
        document.CancelComponentEdit(vector4);
        Require(probe.Value == Vector4.One && !document.IsDirty && !document.CanUndo, "Vector Cancel must restore all axes without a history entry");
        try { document.UpdateComponent(vector4, new Vector4(1, 2, 3, float.NegativeInfinity)); throw new InvalidOperationException("Non-finite fourth axis must be rejected"); }
        catch (ArgumentException) { }
        Require(probe.Value == Vector4.One && !document.IsDirty, "Rejected W must retain the original clean vector");
        using var stream = new MemoryStream();
        Stride.Core.Yaml.AssetYamlSerializer.Default.Serialize(stream, edited4); stream.Position = 0;
        Require((Vector4)Stride.Core.Yaml.AssetYamlSerializer.Default.Deserialize(stream, typeof(Vector4)) == edited4, "Vector4 must preserve float values through the asset YAML serializer");
        owner.Components.Remove(probe);
    }

    [Stride.Core.DataContract]
    private sealed class VectorRegressionComponent : Stride.Engine.EntityComponent
    {
        [Stride.Core.DataMember]
        public Vector4 Value { get; set; } = Vector4.One;
    }

    private static void VerifyProjectVectorTypes(SceneDocument document, string scene, Stride.Engine.Entity owner)
    {
        var script = owner.Components.Single(component => component.GetType().Name == "InspectorTypes");
        var runtime = (Stride.Engine.EntityComponent)Activator.CreateInstance(script.GetType())!;
        foreach (var name in new[] { "ShaderWeights", "PixelOffset", "GridResolution", "ClipRectangle" })
        {
            var field = document.GetComponentFields(script).Single(entry => entry.Name == name);
            var original = field.Node.Retrieve();
            object edited = name switch
            {
                "ShaderWeights" => new Vector4(0.123456789f, -2.5f, 3.75f, 4.125f),
                "PixelOffset" => new Int2(int.MinValue, int.MaxValue),
                "GridResolution" => new Int3(17000001, -17000003, int.MinValue),
                _ => new Int4(int.MaxValue, int.MinValue, 17000001, -17000003),
            };
            Require(field.SupportsEditing && field.Children.Count == 0, "All vector families must be atomic editable Quantum fields");
            document.UpdateComponent(field, edited); field.WriteRuntime(runtime, field.Node.Retrieve());
            Require(Equals(field.ReadRuntime(runtime), edited), "Vector runtime propagation must preserve exact integer ranges and every axis");
            document.CancelComponentEdit(field);
            Require(Equals(field.Node.Retrieve(), original) && !document.IsDirty && !document.CanUndo, "Vector Cancel must preserve original clean state and history");
            document.UpdateComponent(field, edited); document.EndComponentEdit();
            document.Undo(); Require(Equals(field.Node.Retrieve(), original) && !document.IsDirty, "Vector undo must restore every original axis");
            document.Redo(); document.Save();
            var saved = new SceneDocument(scene).Asset.Hierarchy.Parts[owner.Id].Entity.Components.Single(component => component.Id == script.Id);
            Require(Equals(field.ReadRuntime(saved), edited), "Real project vector properties must retain exact values and component identity through scene YAML");
            document.Undo(); document.Save();
            Require(Equals(field.Node.Retrieve(), original) && !document.IsDirty, "Vector regression must restore the original fixture data");
        }
    }

    private static void VerifyNumericRanges(SceneDocument document, string scene, Stride.Engine.Entity owner)
    {
        var camera = document.Asset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Camera").Entity.Get<Stride.Engine.CameraComponent>();
        var fov = document.GetComponentFields(camera).Single(entry => entry.Name == nameof(camera.VerticalFieldOfView));
        Require(fov.NumericRange is { SmallStep: 1, LargeStep: 10, Format: "%.0f" }, "Real camera numeric metadata must be cached from the registered descriptor");
        document.UpdateComponent(fov, 200f); document.EndComponentEdit();
        Require(camera.VerticalFieldOfView == 179f, "Camera upper-bound input must normalize before the Quantum mutation");
        document.Undo(); Require(camera.VerticalFieldOfView == 45f && !document.IsDirty, "Normalized edit must undo to its original clean save point");
        document.UpdateComponent(fov, -5f); document.EndComponentEdit();
        Require(camera.VerticalFieldOfView == 1f, "Camera lower-bound input must normalize inclusively");
        document.Undo(); document.UpdateComponent(fov, 64.5f); document.EndComponentEdit();
        Require(camera.VerticalFieldOfView == 64f, "Explicit decimal metadata must match WPF's midpoint-to-even rounding");
        document.Save();
        Require(new SceneDocument(scene).Asset.Hierarchy.Parts[camera.Entity.Id].Entity.Get<Stride.Engine.CameraComponent>().VerticalFieldOfView == 64f, "Normalized numeric values must persist to the actual scene asset");
        document.Undo(); document.Save();
        try { document.UpdateComponent(fov, float.PositiveInfinity); throw new InvalidOperationException("Infinity must be rejected before range clamping"); }
        catch (ArgumentException) { }
        Require(camera.VerticalFieldOfView == 45f && !document.IsDirty, "Non-finite range input must not alter authored data or dirty state");
        var light = document.Asset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Directional light").Entity.Get<Stride.Engine.LightComponent>();
        var pssm = SceneDocument.Flatten(document.GetComponentFields(light)).Single(entry => entry.Name == "Type.Shadow.PartitionMode.PSSMFactor");
        document.UpdateComponent(pssm, 0.045d); document.EndComponentEdit();
        Require(Equals(pssm.Node.Retrieve(), 0.04f), "Decimal text must round in double precision before narrowing to the engine float property");
        document.Undo(); Require(Equals(pssm.Node.Retrieve(), 0.5f) && !document.IsDirty, "Parsed decimal range undo must restore its clean original");
        var script = owner.Components.Single(component => component.GetType().Name == "InspectorTypes");
        var count = document.GetComponentFields(script).Single(entry => entry.Name == "SampleCount");
        document.UpdateComponent(count, int.MaxValue); document.EndComponentEdit();
        Require(Equals(count.Node.Retrieve(), 10), "Integer metadata must normalize without changing the numeric type");
        document.UpdateComponent(count, 2147483648d); document.EndComponentEdit();
        Require(Equals(count.Node.Retrieve(), 10), "Oversized parsed integer input must clamp before narrowing, not overflow to the lower bound");
        document.Undo(); document.UpdateComponent(count, int.MinValue);
        Require(Equals(count.Node.Retrieve(), 1), "Integer minimum input must normalize to its inclusive lower bound");
        document.CancelComponentEdit(count); Require(Equals(count.Node.Retrieve(), 4) && !document.IsDirty, "Cancel must restore an original bounded integer without history");
        var exposure = document.GetComponentFields(script).Single(entry => entry.Name == "ExposureLimit");
        document.UpdateComponent(exposure, 1.236d);
        Require(Equals(exposure.Node.Retrieve(), 1.24d), "Double metadata must retain its declared decimal precision");
        document.CancelComponentEdit(exposure); Require(Equals(exposure.Node.Retrieve(), 0.5d) && !document.IsDirty, "Double Cancel must retain the original save point");
        var runtime = (Stride.Engine.EntityComponent)Activator.CreateInstance(script.GetType())!;
        document.UpdateComponent(exposure, 200d); document.EndComponentEdit(); exposure.WriteRuntime(runtime, exposure.Node.Retrieve());
        Require(Equals(exposure.ReadRuntime(runtime), 2d), "Bounded doubles must propagate normalized runtime values");
        document.Undo();
    }
}
