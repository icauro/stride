using System.Text.Json;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Rendering.Lights;

namespace Stride.Editor.ImGui;

// Service-level integration cases. UI input coverage belongs to the UI runner.
internal static partial class CoreCapabilityScenarios
{
    public static IReadOnlyList<string> Cases { get; } = ["C01.scalar", "C01.enum", "C01.vector", "C02", "C03", "C04", "C05", "C06"];

    public static int Run(string scenarioId, string packagePath, string artifactsDirectory)
    {
        try
        {
            RunCore(scenarioId, packagePath, artifactsDirectory);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            try
            {
                Directory.CreateDirectory(artifactsDirectory);
                File.WriteAllText(Path.Combine(artifactsDirectory, "error.txt"), error.ToString());
                var resultPath = Path.Combine(artifactsDirectory, "result.json");
                // RunCore records the retained fixture. Setup failures still get a result.
                if (!File.Exists(resultPath))
                    File.WriteAllText(resultPath, JsonSerializer.Serialize(new { scenarioId, passed = false, error = error.ToString() }));
            }
            catch (Exception reportingError) { Console.Error.WriteLine(reportingError); }
            return 1;
        }
    }

    private static void RunCore(string scenarioId, string packagePath, string artifactsDirectory)
    {
        if (!Cases.Contains(scenarioId)) throw new ArgumentException("Unknown core scenario", nameof(scenarioId));
        Directory.CreateDirectory(artifactsDirectory);
        // Never allow a previous successful attempt to masquerade as this run.
        File.WriteAllText(Path.Combine(artifactsDirectory, "result.json"), JsonSerializer.Serialize(new { scenarioId, passed = false, phase = "preparing" }));
        var copiedPackage = UiWorkflowTest.CopyFixture(packagePath);
        var fixtureRoot = Path.GetDirectoryName(copiedPackage)!;
        var passed = false;
        try
        {
            var workspace = Environment.GetEnvironmentVariable("STRIDE_EDITOR_PREPARED_AUTHORED_FIXTURE") == "1"
                ? PreparedAuthoredFixture.Open(copiedPackage) : new ProjectWorkspace(copiedPackage);
            try
            {
                var source = workspace.Assets.First(asset => asset.Extension == ".sdscene").Path;
                if (scenarioId.StartsWith("C01.", StringComparison.Ordinal)) EditValues(source, scenarioId);
                else if (scenarioId == "C02") CollectionsAndReferences(source);
                else if (scenarioId == "C03") EntityLifecycle(source);
                else if (scenarioId == "C06") SaveFailureCapabilityScenario.Run(source);
                else if (scenarioId == "C04") PrefabInheritance(source);
                else History(source);
            }
            finally { workspace.ReleaseUnadopted(); }
            passed = true;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(artifactsDirectory, scenarioId + ".failure.json"),
                JsonSerializer.Serialize(new { Scenario = scenarioId, Fixture = fixtureRoot, Error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }
        finally
        {
            // Retain failing fixtures. Cleanup errors fail the caller rather than hiding a leak.
            try
            {
                if (passed) Directory.Delete(fixtureRoot, true);
            }
            catch (Exception cleanupError)
            {
                passed = false;
                File.WriteAllText(Path.Combine(artifactsDirectory, scenarioId + ".failure.json"),
                    JsonSerializer.Serialize(new { scenarioId, fixture = fixtureRoot, error = cleanupError.ToString() }));
                throw;
            }
            finally
            {
                File.WriteAllText(Path.Combine(artifactsDirectory, "result.json"), JsonSerializer.Serialize(new
                {
                    scenarioId,
                    passed,
                    fixture = passed ? null : fixtureRoot,
                    coverage = "Core graph/history/persistence integration; no synthetic or native UI input",
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
    }

    private static void EditValues(string path, string scenario)
    {
        using var document = new SceneDocument(path);
        var light = document.Asset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Directional light").Entity.Get<LightComponent>();
        document.Selected = light.Entity;
        var fields = SceneDocument.Flatten(document.GetComponentFields(light)).ToArray();
        var field = fields.Single(value => value.Name == (scenario switch
        {
            "C01.scalar" => "Intensity",
            "C01.enum" => "Type.Shadow.Size",
            _ => "Type.Color.Value",
        }));
        var original = field.Node.Retrieve();
        object next = scenario switch
        {
            "C01.scalar" => 7f,
            "C01.enum" => LightShadowMapSize.Medium,
            _ => new Color3(1.125f, 2.25f, 0.875f),
        };
        Require(!Equals(original, next), "Fixture must differ from requested value");
        object intermediate = scenario switch
        {
            "C01.scalar" => 8f,
            "C01.enum" => LightShadowMapSize.XLarge,
            _ => new Color3(1.5f, 2.5f, 0.75f),
        };
        document.UpdateComponent(field, intermediate);
        document.UpdateComponent(field, next);
        document.EndComponentEdit();
        Require(Equals(field.Node.Retrieve(), next) && document.IsDirty, "Actual graph must contain committed value");
        document.Undo();
        Require(Equals(field.Node.Retrieve(), original) && !document.CanUndo && !document.IsDirty,
            "A continuous gesture must create exactly one transaction");
        document.Redo(); document.Save();
        using (var reopened = new SceneDocument(path))
        {
            var savedLight = reopened.Asset.Hierarchy.Parts[light.Entity.Id].Entity.Components.Single(component => component.Id == light.Id);
            var savedField = SceneDocument.Flatten(reopened.GetComponentFields(savedLight)).Single(value => value.Name == field.Name);
            Require(Equals(savedField.Node.Retrieve(), next), "Independent disk reload must retain actual graph value and component ID");
        }
        document.Undo(); document.Save();
        if (scenario != "C01.enum")
        {
            object invalid = scenario == "C01.scalar" ? float.NaN : new Color3(float.PositiveInfinity, 1, 1);
            try { document.UpdateComponent(field, invalid); }
            catch (ArgumentException) { invalid = null!; }
            Require(invalid == null && Equals(field.Node.Retrieve(), original) && !document.IsDirty,
                "Nonfinite input must be rejected without authored mutation");
        }
        if (scenario == "C01.scalar")
        {
            var camera = document.Asset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Camera").Entity.Get<CameraComponent>();
            document.Selected = camera.Entity;
            var fov = document.GetComponentFields(camera).Single(value => value.Name == "VerticalFieldOfView");
            var initial = fov.Node.Retrieve();
            document.UpdateComponent(fov, 200f); document.EndComponentEdit();
            Require(camera.VerticalFieldOfView == 179f, "Finite range input must normalize to WPF-equivalent bound");
            document.Undo(); Require(Equals(fov.Node.Retrieve(), initial) && !document.IsDirty, "Range normalization must be undoable");
            document.UpdateComponent(fov, -5f); document.EndComponentEdit();
            Require(camera.VerticalFieldOfView == 1f, "Finite range input must normalize to inclusive lower bound");
            document.Undo();
            var position = document.History.Position;
            var rejected = false;
            try { document.UpdateComponent(fov, float.PositiveInfinity); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected && Equals(fov.Node.Retrieve(), initial) && document.History.Position == position && !document.IsDirty,
                "Nonfinite input must reject before bounded normalization and preserve history boundary");
        }
    }

    private static void EntityLifecycle(string path)
    {
        using var document = new SceneDocument(path);
        var roots = document.Asset.Hierarchy.RootParts.Select(entity => entity.Id).ToArray();
        var parent = document.CreateEmptyEntity();
        document.Rename(parent, "Lifecycle – 星");
        document.Selected = parent;
        document.BeginTransformEdit();
        parent.Transform.Position = new Vector3(10, 2, -4);
        parent.Transform.Scale = new Vector3(2);
        parent.Transform.Rotation = Quaternion.RotationYawPitchRoll(0.3f, 0.2f, -0.1f);
        document.EndTransformEdit();
        var child = document.CreateEmptyEntity();
        document.BeginTransformEdit();
        child.Transform.Position = new Vector3(1, 2, 3);
        child.Transform.Rotation = Quaternion.RotationYawPitchRoll(-0.2f, 0.1f, 0.4f);
        document.EndTransformEdit();
        child.Transform.UpdateWorldMatrix(); var world = child.Transform.WorldMatrix;
        var childId = child.Id; var componentId = child.Transform.Id;
        document.Reparent(child, parent, true); child.Transform.UpdateWorldMatrix();
        Require(Near(world, child.Transform.WorldMatrix), "Keep-world reparent must preserve actual world matrix");
        Require(!document.CanReparent(parent, child), "Cycle must be ineligible");
        var structure = document.StructureVersion;
        var rejected = false;
        try { document.Reparent(parent, child, false); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected && document.StructureVersion == structure && parent.Transform.Parent == null, "Rejected cycle must leave topology intact");
        document.Save();
        document.DeleteEntity(parent);
        Require(!document.Asset.Hierarchy.Parts.ContainsKey(parent.Id) && !document.Asset.Hierarchy.Parts.ContainsKey(childId), "Delete must remove complete subtree");
        document.Undo();
        Require(ReferenceEquals(document.Asset.Hierarchy.Parts[childId].Entity, child) && child.Transform.Id == componentId && !document.IsDirty,
            "Undo deletion must restore saved identities and clean revision");
        document.Redo(); document.Save();
        using (var deleted = new SceneDocument(path))
            Require(!deleted.Asset.Hierarchy.Parts.ContainsKey(parent.Id) && !deleted.Asset.Hierarchy.Parts.ContainsKey(childId)
                && deleted.Asset.Hierarchy.RootParts.Select(entity => entity.Id).SequenceEqual(roots),
                "Saved subtree deletion must remove all designs and restore original root order on independent reload");
        document.Undo();
        Require(document.IsDirty && ReferenceEquals(document.Asset.Hierarchy.Parts[childId].Entity, child),
            "Undo saved deletion must restore actual subtree and leave the disk save point");
        document.Save();
        using var reopened = new SceneDocument(path);
        var saved = reopened.Asset.Hierarchy.Parts[childId].Entity;
        Require(saved.Transform.Parent?.Entity.Id == parent.Id && saved.Transform.Id == componentId
            && reopened.Asset.Hierarchy.Parts[parent.Id].Entity.Name == "Lifecycle – 星", "Reload must preserve subtree, rename and component IDs");
        saved.Transform.UpdateWorldMatrix();
        Require(Near(world, saved.Transform.WorldMatrix), "Keep-world result must survive serialization with rotated/scaled destination");
        Require(reopened.Asset.Hierarchy.RootParts.Take(roots.Length).Select(entity => entity.Id).SequenceEqual(roots), "Existing root order must survive lifecycle edits");
    }

    private static void History(string path)
    {
        var secondPath = path + ".history-other.sdscene";
        File.Copy(path, secondPath);
        using var first = new SceneDocument(path);
        using var second = new SceneDocument(secondPath);
        var entity = first.Asset.Hierarchy.RootParts.First();
        var other = second.Asset.Hierarchy.RootParts.First();
        var name = entity.Name;
        first.Rename(entity, name + " first"); first.Save();
        second.Rename(other, name + " second");
        first.Undo();
        Require(first.IsDirty && second.IsDirty && other.Name == name + " second", "Undo in one document must not affect another");
        first.Redo(); Require(!first.IsDirty, "Redo to save point must become clean");
        first.Undo(); first.Rename(entity, name + " branch");
        Require(!first.CanRedo && first.IsDirty, "Editing after undo must discard old redo and saved branch identity");
        var light = first.Asset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Directional light").Entity.Get<LightComponent>();
        var intensity = first.GetComponentFields(light).Single(field => field.Name == "Intensity");
        first.Selected = light.Entity;
        var original = intensity.Node.Retrieve();
        var historyPosition = first.History.Position;
        first.UpdateComponent(intensity, 123f); first.CancelComponentEdit(intensity);
        Require(Equals(intensity.Node.Retrieve(), original) && first.History.Position == historyPosition && first.IsDirty,
            "Cancel must restore value without adding history or clearing existing dirty edits");
        second.Undo(); Require(!second.IsDirty && entity.Name == name + " branch", "Other history must remain independent");
        first.Save();
        using var reopened = new SceneDocument(path);
        Require(reopened.Asset.Hierarchy.Parts[entity.Id].Entity.Name == name + " branch" && !reopened.IsDirty, "Branch edit must reopen at independent clean state");
        CanonicalSessionHistory(path, secondPath);
    }

    private static bool Near(Matrix a, Matrix b) => a.ToArray().Zip(b.ToArray()).All(pair => Math.Abs(pair.First - pair.Second) < 0.0001f);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
