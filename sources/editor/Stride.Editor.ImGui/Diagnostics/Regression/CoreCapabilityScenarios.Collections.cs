using System.Collections;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal static partial class CoreCapabilityScenarios
{
    private static void CollectionsAndReferences(string path)
    {
        using var document = new SceneDocument(path);
        var sphere = document.Asset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Sphere").Entity;
        var cube = document.Asset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Imported cube").Entity;
        var ground = document.Asset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Ground").Entity;
        var script = sphere.Components.Single(component => component.GetType().Name == "InspectorTypes");
        document.Selected = sphere;
        ComponentField Field(string name) => document.GetComponentFields(script).Single(field => field.Name == name);
        ComponentField Reference() => Field("TargetEntity");
        ComponentField TransformReference() => Field("TargetTransform");
        ComponentField Collection() => Field("TargetEntities");
        IList Values() => (IList)Collection().Node.Retrieve()!;
        var originalTargets = Values().Cast<Entity?>().Select(entity => entity?.Id).ToArray();
        var originalItems = document.GetCollectionItems(Collection()).Select(item => item.Id).ToArray();
        Require(ReferenceEquals(Reference().Node.Retrieve(), cube), "Fixture must reference actual imported entity");
        Require(document.CanAddCollection(Collection()) && document.CanRemoveCollection(Collection()) && document.CanReorderCollection(Collection()),
            "Fixture collection must expose real add/remove/reorder commands");

        var historyPosition = document.History.Position;
        var rejected = false;
        try { document.UpdateComponent(Reference(), new Entity("Foreign") { Id = cube.Id }); }
        catch (ArgumentException) { rejected = true; }
        Require(rejected && ReferenceEquals(Reference().Node.Retrieve(), cube) && document.History.Position == historyPosition && !document.IsDirty,
            "Foreign scalar reference must reject before graph/history mutation");
        rejected = false;
        try { document.AddCollectionReference(Collection(), new Entity("Foreign") { Id = cube.Id }); }
        catch (ArgumentException) { rejected = true; }
        Require(rejected && Values().Count == originalTargets.Length && !document.IsDirty, "Foreign collection reference must reject without mutation");

        var selfReference = document.GetCollectionItems(Collection()).Single(item => ReferenceEquals(item.Value, sphere));
        var selfId = selfReference.Id;
        var beforeStructure = document.StructureVersion;
        var observedNativeCollectionEvents = 0;
        void ObserveNativeCollection(object? sender, Stride.Core.Assets.Quantum.AssetItemNodeChangeEventArgs change)
        {
            if (ReferenceEquals(change.Collection, selfReference.Collection)) observedNativeCollectionEvents++;
        }
        document.HierarchyGraph.ItemChanged += ObserveNativeCollection;
        try { document.MoveCollectionItem(selfReference, 0); }
        finally { document.HierarchyGraph.ItemChanged -= ObserveNativeCollection; }
        Require(ReferenceEquals(Values()[0], sphere) && document.GetCollectionItems(Collection()).First().Id == selfId
            && document.StructureVersion > beforeStructure && observedNativeCollectionEvents == 2,
            "Reordering a reference to its own component owner must retain graph listeners and invalidate cached order");
        document.Undo();
        Require(document.GetCollectionItems(Collection()).Select(item => item.Id).SequenceEqual(originalItems)
            && Values().Cast<Entity?>().Select(entity => entity?.Id).SequenceEqual(originalTargets) && !document.IsDirty,
            "Self-reference reorder undo must restore actual values, stable item IDs, order and clean revision");

        var observedOwnerChanges = 0;
        void ObserveOwner(object? sender, Stride.Core.Assets.Quantum.AssetMemberNodeChangeEventArgs change)
        {
            if (change.Member.Name == "TargetEntity" && ReferenceEquals(change.Member.Parent.Retrieve(), script)) observedOwnerChanges++;
        }
        document.HierarchyGraph.Changed += ObserveOwner;
        try { document.UpdateComponent(Reference(), ground); document.EndComponentEdit(); }
        finally { document.HierarchyGraph.Changed -= ObserveOwner; }
        Require(observedOwnerChanges > 0, "Subsequent owner member changes must remain observed after self-reference reorder and undo");
        document.Undo(); Require(ReferenceEquals(Reference().Node.Retrieve(), cube) && !document.IsDirty, "Reference undo must restore original object");
        document.Redo();
        var initialTransform = TransformReference().Node.Retrieve();
        rejected = false;
        try { document.UpdateComponent(TransformReference(), new Entity("Foreign") { Id = ground.Id }.Transform); }
        catch (ArgumentException) { rejected = true; }
        Require(rejected && ReferenceEquals(TransformReference().Node.Retrieve(), initialTransform),
            "Same-ID foreign component owner must reject before reference mutation");
        document.UpdateComponent(TransformReference(), ground.Transform); document.EndComponentEdit();
        document.Undo(); Require(ReferenceEquals(TransformReference().Node.Retrieve(), initialTransform), "Component reference undo restores exact original target");
        document.Redo();
        document.AddCollectionReference(Collection(), ground);
        var added = document.GetCollectionItems(Collection()).Last();
        var addedId = added.Id;
        Require(ReferenceEquals(added.Value, ground), "Add must retain actual target identity");
        document.MoveCollectionItem(document.GetCollectionItems(Collection()).Single(item => item.Id == addedId), 0);
        Require(document.GetCollectionItems(Collection()).First().Id == addedId && ReferenceEquals(Values()[0], ground),
            "Reordering must retain item identity and move actual graph value");
        document.Undo(); Require(document.GetCollectionItems(Collection()).Last().Id == addedId, "Move undo must restore original item location");
        document.Redo();
        document.RemoveCollectionItem(document.GetCollectionItems(Collection()).First());
        Require(!document.GetCollectionItems(Collection()).Any(item => item.Id == addedId), "Remove must drop the specific item identity");
        document.Undo();
        Require(document.GetCollectionItems(Collection()).First().Id == addedId && ReferenceEquals(Values()[0], ground), "Remove undo restores item and reference");
        document.Save();
        var orderedIds = document.GetCollectionItems(Collection()).Select(item => item.Id).ToArray();
        var orderedTargets = Values().Cast<Entity?>().Select(entity => entity?.Id).ToArray();
        using (var reopened = new SceneDocument(path))
        {
            var savedScript = reopened.Asset.Hierarchy.Parts[sphere.Id].Entity.Components.Single(component => component.Id == script.Id);
            var savedFields = reopened.GetComponentFields(savedScript);
            var savedCollection = savedFields.Single(field => field.Name == "TargetEntities");
            Require(((Entity)savedFields.Single(field => field.Name == "TargetEntity").Node.Retrieve()!).Id == ground.Id,
                "Scalar target GUID must survive independent disk reload");
            Require(((TransformComponent)savedFields.Single(field => field.Name == "TargetTransform").Node.Retrieve()!).Id == ground.Transform.Id,
                "Component reference must preserve actual target component GUID on reload");
            Require(reopened.GetCollectionItems(savedCollection).Select(item => item.Id).SequenceEqual(orderedIds)
                && ((IList)savedCollection.Node.Retrieve()!).Cast<Entity?>().Select(entity => entity?.Id).SequenceEqual(orderedTargets),
                "Reload must retain collection item IDs, target IDs and order");
        }

        document.DeleteEntity(ground);
        Require(Reference().Node.Retrieve() == null && TransformReference().Node.Retrieve() == null && Values()[0] == null && Values().Count == orderedTargets.Length,
            "Entity deletion must clear scalar/list references without removing collection slots");
        document.Undo();
        Require(ReferenceEquals(Reference().Node.Retrieve(), ground) && ReferenceEquals(TransformReference().Node.Retrieve(), ground.Transform)
            && ReferenceEquals(Values()[0], ground) && !document.IsDirty,
            "Deletion undo restores exact reference objects and saved revision");
        document.Redo(); document.Save();
        using var deleted = new SceneDocument(path);
        var deletedScript = deleted.Asset.Hierarchy.Parts[sphere.Id].Entity.Components.Single(component => component.Id == script.Id);
        var deletedFields = deleted.GetComponentFields(deletedScript);
        Require(!deleted.Asset.Hierarchy.Parts.ContainsKey(ground.Id)
            && deletedFields.Single(field => field.Name == "TargetEntity").Node.Retrieve() == null
            && deletedFields.Single(field => field.Name == "TargetTransform").Node.Retrieve() == null
            && ((IList)deletedFields.Single(field => field.Name == "TargetEntities").Node.Retrieve()!)[0] == null,
            "Independent reload must contain no dangling deleted entity references");
        Require(originalItems.Length == originalTargets.Length, "Initial collection IDs must correspond to actual serialized slots");
    }
}
