using Stride.Core.Mathematics;

namespace Stride.Editor.ImGui;

internal static class HierarchyReparentingRegression
{
    internal static void Run(string scene)
    {
        var testPath = scene + ".move-test.sdscene"; File.Copy(scene, testPath, true);
        try
        {
            using var document = new SceneDocument(testPath);
            var sphere = document.Asset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Sphere").Entity;
            var ground = document.Asset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Ground").Entity;
            var cube = document.Asset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Imported cube").Entity;
            ground.Transform.Position = new Vector3(10, 2, -4); ground.Transform.Scale = new Vector3(2); ground.Transform.Rotation = Quaternion.RotationYawPitchRoll(0.3f, 0, 0);
            document.Save();
            var local = TransformState.Read(sphere); var roots = document.Asset.Hierarchy.RootParts.ToArray();
            var ids = document.Asset.Hierarchy.Parts.Keys.ToArray(); var components = sphere.Components.ToArray();
            document.Selected = sphere;
            document.Reparent(sphere, ground, false);
            Require(document.IsDirty && sphere.Transform.Parent == ground.Transform && TransformState.Read(sphere) == local
                && components.SequenceEqual(sphere.Components) && ids.SequenceEqual(document.Asset.Hierarchy.Parts.Keys), "Local move must retain subtree identities/components and local TRS");
            Require(!document.CanReparent(ground, sphere) && !document.CanReparent(sphere, sphere), "Cycles and self-parenting must be rejected");
            document.Undo(); Require(!document.IsDirty && roots.SequenceEqual(document.Asset.Hierarchy.RootParts) && sphere.Transform.Parent == null, "Undo must restore exact root order and clean revision");
            sphere.Transform.UpdateWorldMatrix(); var world = sphere.Transform.WorldMatrix;
            document.Reparent(sphere, ground, true); sphere.Transform.UpdateWorldMatrix();
            Require(Near(world, sphere.Transform.WorldMatrix), "World-preserving move must keep world pose with translated/rotated/uniform-scaled parent");
            document.Save(); using var reopened = new SceneDocument(testPath);
            var saved = reopened.Asset.Hierarchy.Parts[sphere.Id].Entity; saved.Transform.UpdateWorldMatrix();
            Require(saved.Transform.Parent?.Entity.Id == ground.Id && Near(world, saved.Transform.WorldMatrix), "Saved moved hierarchy/world pose must round-trip");
            document.Undo(); Require(document.IsDirty && TransformState.Read(sphere) == local, "Undo saved move restores original TRS and becomes dirty");
            document.Redo(); Require(!document.IsDirty, "Redo reaches saved move revision");
            document.Reparent(sphere, null, true); sphere.Transform.UpdateWorldMatrix(); Require(Near(world, sphere.Transform.WorldMatrix), "Moving back to root can retain world pose");
            document.Undo(); document.Selected = ground; var child = document.CreateEmptyEntity(ground);
            document.Reparent(ground, cube, false); Require(child.Transform.Parent == ground.Transform && document.Asset.Hierarchy.Parts.ContainsKey(child.Id), "Moving subtree must retain descendant topology/designs");
            document.Undo(); Require(ground.Transform.Parent == null && child.Transform.Parent == ground.Transform, "Undo subtree move retains children");
            document.Undo(); document.Reparent(sphere, null, false); document.Selected = ground; ground.Transform.Scale = Vector3.Zero;
            var version = document.StructureVersion;
            try { document.Reparent(sphere, ground, true); throw new Exception("Singular destination accepted"); }
            catch (InvalidOperationException) { }
            Require(document.StructureVersion == version, "Invalid world conversion must not mutate hierarchy/history");
            Console.WriteLine("HIERARCHY REPARENT REGRESSION PASS: local/world TRS, reference/object identity, cycle guards, root ordering, nested subtree history and save/reload.");
        }
        finally { File.Delete(testPath); }
    }
    private static bool Near(Matrix a, Matrix b) => a.ToArray().Zip(b.ToArray()).All(pair => Math.Abs(pair.First - pair.Second) < 0.0001f);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
