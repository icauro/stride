using Stride.Engine;

namespace Stride.Editor.ImGui;

internal static class HierarchyCreationRegression
{
    internal static void Run(string scene)
    {
        var testPath = scene + ".hierarchy-test.sdscene";
        File.Copy(scene, testPath, true);
        try
        {
            using var document = new SceneDocument(testPath);
            var originals = document.Asset.Hierarchy.RootParts.ToArray();
            var originalIds = document.Asset.Hierarchy.Parts.Keys.ToArray();
            var selection = originals.First(); document.Selected = selection;
            var root = document.CreateEmptyEntity(); var rootId = root.Id; var componentId = root.Transform.Id;
            Require(document.IsDirty && document.Selected == root && document.Asset.Hierarchy.RootParts.Last() == root, "Root creation appends and selects the authored entity");
            var child = document.CreateEmptyEntity(root);
            Require(child.Name == root.Name && child.Transform.Parent == root.Transform && child.Scene == null, "Duplicate names and child transform ownership must be retained");
            document.Undo(); Require(document.Selected == root && !document.Asset.Hierarchy.Parts.ContainsKey(child.Id), "Undo child restores previous selection and removes its design");
            document.Undo(); Require(!document.IsDirty && document.Selected == selection && originals.SequenceEqual(document.Asset.Hierarchy.RootParts)
                && originalIds.SequenceEqual(document.Asset.Hierarchy.Parts.Keys), "Undo both creations restores exact original hierarchy/save point");
            document.Redo(); document.Redo();
            Require(ReferenceEquals(document.Asset.Hierarchy.Parts[rootId].Entity, root) && root.Transform.Id == componentId && document.Selected == child,
                "Redo retains entity/component identity and child selection");
            document.Save();
            using var reopened = new SceneDocument(testPath);
            Require(!document.IsDirty && reopened.Asset.Hierarchy.Parts[child.Id].Entity.Transform.Parent?.Entity.Id == rootId
                && reopened.Asset.Hierarchy.Parts[rootId].Entity.Transform.Id == componentId, "Save/reopen retains parent and component IDs");
            document.Undo(); Require(document.IsDirty, "Undo after save must leave the saved revision");
            document.Redo(); Require(!document.IsDirty, "Redo to saved creation must restore clean state");
            var version = document.StructureVersion;
            try { document.CreateEmptyEntity(new Entity("Foreign")); throw new Exception("Foreign parent was accepted"); }
            catch (InvalidOperationException) { }
            Require(version == document.StructureVersion && !document.IsDirty, "Rejected foreign parent must not create history or mutate structure");
            document.Undo(); document.Save();
            using var rootOnly = new SceneDocument(testPath);
            Require(rootOnly.Asset.Hierarchy.Parts.ContainsKey(rootId) && !rootOnly.Asset.Hierarchy.Parts.ContainsKey(child.Id)
                && rootOnly.Asset.Hierarchy.Parts[rootId].Entity.Transform.Children.Count == 0, "Saving an undone child must remove reference metadata as well as the design");
            document.Undo(); document.Save();
            using var restored = new SceneDocument(testPath);
            Require(originalIds.SequenceEqual(restored.Asset.Hierarchy.Parts.Keys) && restored.Asset.Hierarchy.RootParts.Select(entity => entity.Id).SequenceEqual(originals.Select(entity => entity.Id)),
                "Save after undoing all additions must preserve original hierarchy and references");
            var existingParent = document.Asset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Sphere").Entity;
            var ownedChild = document.CreateEmptyEntity(existingParent);
            document.BeginTransformEdit(); ownedChild.Transform.Position = new(1, 2, 3); document.EndTransformEdit();
            document.Rename(ownedChild, "Child – 星"); document.Save();
            using var existingParentReload = new SceneDocument(testPath);
            var savedChild = existingParentReload.Asset.Hierarchy.Parts[ownedChild.Id].Entity;
            Require(savedChild.Transform.Parent?.Entity.Id == existingParent.Id && savedChild.Transform.Position == new Stride.Core.Mathematics.Vector3(1, 2, 3)
                && savedChild.Name == "Child – 星" && !document.CanRedo, "Creation under existing entities must compose with transform/name edits and discard the old redo branch");
            Console.WriteLine("HIERARCHY CREATION REGRESSION PASS: root/child creation, duplicate names, stable IDs, graph reference metadata, undo/redo/save points, save after removal, existing-parent transforms/rename and rejected foreign parents.");
        }
        finally { File.Delete(testPath); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
