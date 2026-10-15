using System.Collections;
using Stride.Assets.Entities;
using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    internal object VerifyCollectionContextParity()
    {
        var scene = Document ?? throw new InvalidOperationException("Collection context verification requires the actual scene.");
        var history = scene.SessionHistory ?? throw new InvalidOperationException("Collection context verification requires native history.");
        if (!Environment.GetCommandLineArgs().Contains("--ui-test") || scene.IsDirty
            || !System.IO.Path.GetFullPath(scene.Path).StartsWith(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Stride-ImGui-UI") + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Collection context verification requires a clean private fixture.");
        var selected = scene.SelectedIds.ToArray(); var primary = scene.Selected?.Id ?? Guid.Empty;
        scene.Save(); var baseline = File.ReadAllBytes(scene.Path); var boundary = history.Stack.NextUndoTransaction;
        var nativeGraph = scene.HierarchyGraph; var nativeAsset = scene.Asset;
        var entity = nativeAsset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Sphere").Entity;
        SelectCompositeHierarchyEntity(scene, entity, false);
        var type = EntityHierarchyDocument.RegisteredComponentTypes.Single(value => value.FullName == "EditorParity.InspectorTypes");
        var component = entity.Components.FirstOrDefault(value => value.GetType() == type) ?? scene.AddComponent(entity, type);
        HierarchyCollectionContext Root() => scene.GetRootHierarchyCollection(scene.GetComponentFields(component).Single(field => field.Name == "NestedNumbers"))
            ?? throw new InvalidOperationException("The native owned-object list is unavailable.");
        HierarchyCollectionItem Parent() => scene.GetNestedHierarchyItems(Root()).Single();
        HierarchyCollectionContext Context() => scene.GetNestedHierarchyCollection(Parent())
            ?? throw new InvalidOperationException("The actual nested primitive collection is unavailable.");
        HierarchyCollectionItem Item(HierarchyCollectionContext context) => scene.GetNestedHierarchyItems(context).Single();
        scene.ApplyInheritedGraphMutation(scene.PrepareNestedHierarchyAdd(Root(), typeof(List<int>), null), "Arrange owned nested collection parent");
        var parentIdentity = Parent().Id; var parentObject = Parent().Value;
        scene.ApplyInheritedGraphMutation(scene.PrepareNestedHierarchyAdd(Context(), typeof(int), null), "Arrange actual nested collection item");
        var context = Context(); var item = Item(context); var id = item.Id;
        scene.ApplyInheritedGraphMutation(scene.PrepareNestedHierarchyUpdate(item, 41), "Edit actual collection context");
        var edited = history.Stack.NextUndoTransaction;
        history.Undo();
        if (!Equals(((IList)Context().Node.Retrieve()!)[0], 0)) throw new InvalidOperationException("Native collection context Undo lost its value.");
        history.Redo();
        context = Context(); item = Item(context);
        if (!ReferenceEquals(edited, history.Stack.NextUndoTransaction) || item.Id != id || !Equals(item.Value, 41))
            throw new InvalidOperationException("Native collection context Redo lost identity, value or history.");
        var staleUpdate = scene.PrepareNestedHierarchyUpdate(item, 57);
        var staleAdd = scene.PrepareNestedHierarchyAdd(context, typeof(int), null);
        scene.ApplyInheritedGraphMutation(scene.PrepareNestedHierarchyRemove(Parent()), "Remove actual nested collection parent");
        if (scene.CanReadNestedHierarchyCollection(context) || scene.CanEditNestedHierarchyCollection(context))
            throw new InvalidOperationException("A removed owned parent retained a readable or editable nested collection context.");
        void Reject(Action operation)
        {
            var yaml = scene.CaptureAssemblyReload().Yaml; var position = history.Stack.NextUndoTransaction; var disk = File.ReadAllBytes(scene.Path);
            var rejected = false;
            try { scene.ApplyInheritedGraphMutation(operation, "Reject stale collection owner"); }
            catch (InvalidOperationException) { rejected = true; }
            if (!rejected || !yaml.AsSpan().SequenceEqual(scene.CaptureAssemblyReload().Yaml) || !ReferenceEquals(position, history.Stack.NextUndoTransaction)
                || !disk.AsSpan().SequenceEqual(File.ReadAllBytes(scene.Path))) throw new InvalidOperationException("Stale collection context mutation was not rejected atomically.");
        }
        Reject(staleUpdate); Reject(staleAdd);
        history.Undo();
        if (Parent().Id != parentIdentity || !ReferenceEquals(Parent().Value, parentObject))
            throw new InvalidOperationException("Undo lost actual nested parent identity or owned object.");
        context = Context(); item = Item(context);
        scene.ApplyInheritedGraphMutation(scene.PrepareNestedHierarchyUpdate(item, 73), "Edit reacquired collection owner");
        scene.Save();
        var loaded = AssetFileSerializer.Load<SceneAsset>(scene.Path).Asset;
        var savedComponent = loaded.Hierarchy.Parts[entity.Id].Entity.Components.Single(value => value.GetType() == type);
        var savedParents = (IList)type.GetProperty("NestedNumbers")!.GetValue(savedComponent)!;
        if (savedParents.Count != 1 || !Equals(((IList)savedParents[0]!)[0], 73))
            throw new InvalidOperationException("Independent native persistence lost the reacquired collection value.");
        var budget = 64;
        while (!ReferenceEquals(history.Stack.NextUndoTransaction, boundary))
        { if (--budget == 0 || !history.Stack.CanRollback) throw new InvalidOperationException("Collection context fixture cleanup failed."); history.Undo(); }
        scene.Save(); scene.SelectRange(selected, primary);
        if (!baseline.AsSpan().SequenceEqual(File.ReadAllBytes(scene.Path)) || !ReferenceEquals(nativeGraph, scene.HierarchyGraph) || !ReferenceEquals(nativeAsset, scene.Asset))
            throw new InvalidOperationException("Collection context verification did not restore its exact canonical baseline.");
        return new { schema = 1, nativeItemIdentity = true, undoRedo = true, stalePreparedUpdateRejected = true, stalePreparedAddRejected = true,
            removedParentUnreadable = true, parentIdentityRestored = true, freshOwnerEditable = true, nativeSavedRoundtrip = true, baselineRestored = true,
            coverage = "Real nested primitive list under an owned list parent; stale prepared add/update after parent removal, native IDs/history and independent persistence. No widget or cross-document claim." };
    }
}
