using System.Collections;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal static class HierarchyDeletionRegression
{
    internal static void Run(string scene)
    {
        var testPath = scene + ".deletion-test.sdscene"; File.Copy(scene, testPath, true);
        try
        {
            using var document = new SceneDocument(testPath);
            var originalIds = document.Asset.Hierarchy.Parts.Keys.ToArray();
            var rootIds = document.Asset.Hierarchy.RootParts.Select(entity => entity.Id).ToArray();
            var cube = document.Asset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Imported cube").Entity;
            var sphere = document.Asset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Sphere").Entity;
            var script = sphere.Components.Single(component => component.GetType().Name == "InspectorTypes");
            object? Read(string property) => script.GetType().GetProperty(property)!.GetValue(script);
            Require(ReferenceEquals(Read("TargetEntity"), cube) && ReferenceEquals(Read("TargetTransform"), cube.Transform)
                && ReferenceEquals(((IList)Read("TargetEntities")!)[0], cube), "Fixture must contain actual entity/component/list references to the imported model");
            document.Selected = cube; document.DeleteEntity(cube);
            Require(document.IsDirty && document.Selected == null && !document.Asset.Hierarchy.Parts.ContainsKey(cube.Id)
                && Read("TargetEntity") == null && Read("TargetTransform") == null && ((IList)Read("TargetEntities")!)[0] == null
                && ReferenceEquals(((IList)Read("TargetEntities")!)[1], sphere), "Delete must remove the design and clear only references to deleted objects");
            document.Undo();
            Require(!document.IsDirty && document.Selected == cube && originalIds.SequenceEqual(document.Asset.Hierarchy.Parts.Keys)
                && rootIds.SequenceEqual(document.Asset.Hierarchy.RootParts.Select(entity => entity.Id)) && ReferenceEquals(Read("TargetEntity"), cube)
                && ReferenceEquals(Read("TargetTransform"), cube.Transform) && ReferenceEquals(((IList)Read("TargetEntities")!)[0], cube),
                "Undo must restore exact object identities, reference list values, order, selection and original save point");
            document.Redo(); document.Save();
            using var deletedReload = new SceneDocument(testPath);
            var savedScript = deletedReload.Asset.Hierarchy.Parts[sphere.Id].Entity.Components.Single(component => component.Id == script.Id);
            Require(!document.IsDirty && !deletedReload.Asset.Hierarchy.Parts.ContainsKey(cube.Id)
                && savedScript.GetType().GetProperty("TargetEntity")!.GetValue(savedScript) == null, "Saved deletion must round-trip without dangling references");
            document.Undo(); Require(document.IsDirty, "Undo saved deletion must become dirty"); document.Save();
            document.Selected = cube; var child = document.CreateEmptyEntity(cube); var grandchild = document.CreateEmptyEntity(child); document.Save();
            document.Selected = cube; document.DeleteEntity(cube);
            Require(!document.Asset.Hierarchy.Parts.ContainsKey(child.Id) && !document.Asset.Hierarchy.Parts.ContainsKey(grandchild.Id), "Deletion must remove the entire nested subtree");
            document.Undo(); document.Save();
            using var subtreeReload = new SceneDocument(testPath);
            Require(!document.IsDirty && ReferenceEquals(document.Asset.Hierarchy.Parts[cube.Id].Entity, cube)
                && subtreeReload.Asset.Hierarchy.Parts[grandchild.Id].Entity.Transform.Parent?.Entity.Id == child.Id
                && subtreeReload.Asset.Hierarchy.Parts[child.Id].Entity.Transform.Parent?.Entity.Id == cube.Id, "Undo nested deletion and save/reload must restore hierarchy/component identities");
            var revision = document.StructureVersion;
            try { document.DeleteEntity(new Entity("Foreign")); throw new Exception("Foreign entity accepted"); }
            catch (InvalidOperationException) { }
            Require(revision == document.StructureVersion && !document.IsDirty, "Rejected deletion must not mutate scene/history");
            Console.WriteLine("HIERARCHY DELETION REGRESSION PASS: real DeleteParts, entity/component/list reference cleanup and undo, stable order/identity/save points, nested subtree restoration and save/reload.");
        }
        finally { File.Delete(testPath); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
