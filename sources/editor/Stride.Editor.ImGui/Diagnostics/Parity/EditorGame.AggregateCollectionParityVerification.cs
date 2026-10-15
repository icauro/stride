using System.Collections;
using Stride.Assets.Entities;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.IO;
using Stride.Core.Reflection;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    internal object VerifyAggregateCollectionParity()
    {
        var scene = Document ?? throw new InvalidOperationException("Aggregate collection verification requires a canonical scene.");
        var history = sessionHistory!;
        if (!args.Contains("--ui-test") || scene.IsDirty || !scene.Path.StartsWith(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Aggregate collection verification requires a clean private fixture.");
        var originalSelection = scene.SelectedIds.ToArray(); var primary = scene.Selected?.Id ?? Guid.Empty;
        scene.Save();
        var baseline = File.ReadAllBytes(scene.Path); var undoBoundary = history.Stack.NextUndoTransaction;
        var graph = scene.HierarchyGraph; var nativeAsset = scene.Asset;
        var first = scene.Asset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Sphere").Entity;
        var second = scene.Asset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Imported cube").Entity;
        var scriptType = EntityHierarchyDocument.RegisteredComponentTypes.Single(type => type.FullName == "EditorParity.InspectorTypes");
        SelectCompositeHierarchyEntity(scene, first, false);
        var firstScript = first.Components.FirstOrDefault(component => component.GetType() == scriptType) ?? scene.AddComponent(first, scriptType);
        SelectCompositeHierarchyEntity(scene, second, false);
        var secondScript = second.Components.FirstOrDefault(component => component.GetType() == scriptType) ?? scene.AddComponent(second, scriptType);
        CommonComponentProperty Property(Entity other)
        {
            SelectCompositeHierarchyEntity(scene, first, false); SelectCompositeHierarchyEntity(scene, other, true);
            return ReadAggregateProperties().Single(property => property.Fields.Count == 2 && property.Fields.All(field => field.Name == "NamedCounters"));
        }
        CollectionItem Item(ComponentField field) => scene.GetCollectionItems(field).Single(item => Equals(item.Index.Value, "aggregate"));
        string Snapshot(CommonComponentProperty property) => System.Text.Json.JsonSerializer.Serialize(property.Fields.Select(field => new
        { value = field.Node.Retrieve(), ids = scene.GetCollectionItems(field).Select(item => new { item.Id, key = item.Index.Value,
                itemOverride = item.Collection.GetItemOverride(item.Index).ToString(), keyOverride = item.Collection.GetKeyOverride(item.Index).ToString() }).ToArray(),
            contentOverride = ((IAssetMemberNode)field.Node).GetContentOverride().ToString() }).ToArray());
        var property = Property(second);
        ApplyAggregateMutation(property, field => scene.PrepareAggregateCollectionAdd(field, typeof(int), "aggregate"), "Arrange aggregate dictionary items");
        property = Property(second);
        var ids = property.Fields.Select(field => Item(field).Id).ToArray();
        var position = history.Stack.NextUndoTransaction;
        ApplyAggregateMutation(property, field => scene.PrepareAggregateCollectionUpdate(Item(field), 7), "Edit aggregate dictionary values");
        var edited = history.Stack.NextUndoTransaction;
        if (ReferenceEquals(position, edited) || property.Fields.Any(field => !Equals(((IDictionary)field.Node.Retrieve()!)["aggregate"], 7)))
            throw new InvalidOperationException("Aggregate edit did not update both native owners in a transaction.");
        history.Undo();
        if (!ReferenceEquals(history.Stack.NextUndoTransaction, position) || property.Fields.Any(field => !Equals(((IDictionary)field.Node.Retrieve()!)["aggregate"], 0)))
            throw new InvalidOperationException("Aggregate Undo did not restore both native owners in one step.");
        history.Redo();
        property = Property(second); // Native history replay invalidates presenter/field certificates.
        if (!ReferenceEquals(history.Stack.NextUndoTransaction, edited) || !ids.SequenceEqual(property.Fields.Select(field => Item(field).Id)))
            throw new InvalidOperationException("Aggregate Redo lost its transaction or item identities.");
        ApplyAggregateMutation(property, field => scene.PrepareAggregateCollectionUpdate(Item(field), 7), "No-op aggregate dictionary values");
        if (!ReferenceEquals(history.Stack.NextUndoTransaction, edited)) throw new InvalidOperationException("Aggregate no-op added native history.");
        scene.Save();
        var saved = AssetFileSerializer.Load<SceneAsset>(scene.Path).Asset;
        foreach (var entity in new[] { first, second })
        {
            var loadedScript = saved.Hierarchy.Parts[entity.Id].Entity.Components.Single(component => component.GetType() == scriptType);
            if (!Equals(((IDictionary)scriptType.GetProperty("NamedCounters")!.GetValue(loadedScript)!)["aggregate"], 7))
                throw new InvalidOperationException("Independent native aggregate roundtrip lost an owner value.");
        }
        // Prepare genuine changes, invalidate only the second item, then submit
        // both through the same production capture. The first must roll back.
        property = Property(second);
        var firstField = property.Fields.Single(field => ReferenceEquals(field.Component, firstScript));
        var secondField = property.Fields.Single(field => ReferenceEquals(field.Component, secondScript));
        var firstChange = scene.PrepareAggregateCollectionUpdate(Item(firstField), 9);
        var staleSecond = scene.PrepareAggregateCollectionUpdate(Item(secondField), 9);
        scene.RemoveCollectionItem(Item(secondField));
        var beforeStale = Snapshot(property); var staleUndo = history.Stack.NextUndoTransaction;
        var diskBeforeStale = File.ReadAllBytes(scene.Path);
        var staleRejected = false;
        try { ApplyAggregateMutation(property, field => ReferenceEquals(field.Component, firstScript) ? firstChange : staleSecond, "Reject stale aggregate target"); }
        catch (InvalidOperationException) { staleRejected = true; }
        if (!staleRejected || beforeStale != Snapshot(property) || !ReferenceEquals(history.Stack.NextUndoTransaction, staleUndo)
            || !diskBeforeStale.AsSpan().SequenceEqual(File.ReadAllBytes(scene.Path)))
            throw new InvalidOperationException("Stale second-owner failure did not roll back the first owner atomically.");
        history.Undo();
        SelectCompositeHierarchyEntity(scene, second, false);
        var extraction = scene.PreparePrefabExtraction();
        var nativeSceneItem = workspace!.Session.FindAsset(scene.Asset.Id)!;
        var prefabItem = AssetItem.CreateForPackage(nativeSceneItem.Package, new UFile("AggregateCollectionBase"), extraction.Prefab);
        prefabItem.SourceFolder = nativeSceneItem.SourceFolder; extraction.Metadata.CopyInto(prefabItem.YamlMetadata);
        var prefabPath = prefabItem.FullPath.ToOSPath();
        if (File.Exists(prefabPath)) throw new InvalidOperationException("Aggregate prefab fixture is occupied.");
        CreatePreparedAsset(nativeSceneItem.Package, prefabItem, prefabPath, "Arrange aggregate sealed owner");
        var prefabState = authoring!.Find(prefabItem.Id)!; var prefab = (PrefabDocument)prefabState.Document;
        using var lease = prefabState.AcquireView();
        var baseScript = prefab.Asset.Hierarchy.RootParts.Single().Components.Single(component => component.GetType() == scriptType);
        var baseField = prefab.GetComponentFields(baseScript).Single(field => field.Name == "NamedCounters");
        prefab.ApplyInheritedGraphMutation(() => ((IAssetMemberNode)baseField.Node).RestoreContentOverride(OverrideType.Sealed), "Arrange aggregate sealed dictionary");
        prefab.Save();
        var prefabOwnershipBeforeInsertion = history.HierarchyGraphs.InspectBaseOwnership(scene);
        var instance = scene.InsertPrefab(prefab, "AggregateCollectionBase", null, "", -1, false, null).Roots.Single();
        property = Property(instance);
        var sealedField = property.Fields.Single(field => field.Component.Entity == instance);
        if (sealedField.Presentation?.IsReadOnly(sealedField) != true || scene.CanEditAggregateCollection(sealedField))
            throw new InvalidOperationException("The second native aggregate owner does not retain its real inherited seal.");
        var beforeSeal = Snapshot(property); var sealedUndo = history.Stack.NextUndoTransaction;
        var diskBeforeSeal = File.ReadAllBytes(scene.Path); var baseBeforeSeal = File.ReadAllBytes(prefabPath);
        var rejected = false;
        try { ApplyAggregateMutation(property, field => scene.PrepareAggregateCollectionUpdate(Item(field), 11), "Reject sealed aggregate owner"); }
        catch (InvalidOperationException) { rejected = true; }
        if (!rejected || beforeSeal != Snapshot(property) || !ReferenceEquals(history.Stack.NextUndoTransaction, sealedUndo)
            || !diskBeforeSeal.AsSpan().SequenceEqual(File.ReadAllBytes(scene.Path)) || !baseBeforeSeal.AsSpan().SequenceEqual(File.ReadAllBytes(prefabPath)))
            throw new InvalidOperationException("Sealed second owner changed an aggregate graph or history.");
        var limit = 128;
        while (!ReferenceEquals(history.Stack.NextUndoTransaction, undoBoundary))
        { if (--limit == 0 || !history.Stack.CanRollback) throw new InvalidOperationException("Aggregate fixture history cleanup failed."); history.Undo(); }
        scene.Save(); scene.SelectRange(originalSelection, primary);
        if (!baseline.AsSpan().SequenceEqual(File.ReadAllBytes(scene.Path)) || !ReferenceEquals(graph, scene.HierarchyGraph) || !ReferenceEquals(nativeAsset, scene.Asset)
            || prefabState.IsPublished || workspace.Session.FindAsset(prefabItem.Id) != null || File.Exists(prefabPath))
            throw new InvalidOperationException("Aggregate collection fixture did not restore its native baseline and owners.");
        return new { schema = 1, ownerCount = 2, transactionAtomic = true, itemIdentitiesRetained = true, noopHistory = true,
            savedNativeRoundtrip = true, staleSecondRollback = true, sealedSecondPreflightRejection = true, baselineRestored = true,
            prefabOwnershipBeforeInsertion,
            coverage = "Production aggregate mutation service over two actual components in one canonical scene; no widget clicks or cross-document claim." };
    }
}
