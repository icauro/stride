namespace Stride.Editor.ImGui;

internal static class HierarchyOrderingRegression
{
    internal static void Run(string scene)
    {
        var path = scene + ".order-test.sdscene"; File.Copy(scene, path, true);
        try
        {
            using var document = new SceneDocument(path);
            var roots = document.Asset.Hierarchy.RootParts.ToArray();
            var sphere = roots.Single(entity => entity.Name == "Sphere"); var ground = roots.Single(entity => entity.Name == "Ground");
            var before = TransformState.Read(sphere);
            document.Selected = sphere;
            document.MoveAtIndex(sphere, null, document.Asset.Hierarchy.RootParts.IndexOf(ground), false);
            Require(document.Asset.Hierarchy.RootParts.IndexOf(sphere) + 1 == document.Asset.Hierarchy.RootParts.IndexOf(ground)
                && TransformState.Read(sphere) == before && document.IsDirty, "Root reorder before sibling must preserve local state");
            document.Undo(); Require(roots.SequenceEqual(document.Asset.Hierarchy.RootParts) && !document.IsDirty, "Undo restores original root order/save point");
            document.MoveAtIndex(sphere, null, 0, false); document.Save();
            using var loaded = new SceneDocument(path);
            Require(loaded.Asset.Hierarchy.RootParts[0].Id == sphere.Id, "Root insertion order must persist");
            document.Undo(); Require(document.IsDirty && roots.SequenceEqual(document.Asset.Hierarchy.RootParts), "Undo saved reorder preserves original order");
            document.Redo(); Require(!document.IsDirty, "Redo returns to saved reorder");
            document.Selected = ground; var first = document.CreateEmptyEntity(ground); var second = document.CreateEmptyEntity(ground); document.Save();
            document.MoveAtIndex(first, ground, 2, false);
            Require(ground.Transform.Children[0].Entity == second && ground.Transform.Children[1].Entity == first, "Same-parent insertion must adjust the removal index");
            Require(!document.CanMoveAtIndex(first, ground, 1) && !document.CanMoveAtIndex(first, ground, 2), "Both unchanged insertion slots must be no-ops");
            document.Undo(); Require(!document.IsDirty && ground.Transform.Children[0].Entity == first, "Undo child ordering restores saved state");
            var revision = document.StructureVersion;
            try { document.MoveAtIndex(ground, first, 0, false); throw new Exception("Cycle accepted"); } catch (InvalidOperationException) { }
            Require(document.StructureVersion == revision && !document.IsDirty && !document.CanMoveAtIndex(first, ground, -1), "Invalid insertion/cycle must not mutate scene/history");
            Console.WriteLine("HIERARCHY ORDERING REGRESSION PASS: before/after insertion, same-parent index normalization, no-op/cycle guards, stable transforms and undo/redo/save order.");
        }
        finally { File.Delete(path); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
