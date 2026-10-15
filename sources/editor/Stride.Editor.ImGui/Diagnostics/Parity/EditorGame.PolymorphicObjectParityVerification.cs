using Stride.Assets.Entities;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    internal object VerifyPolymorphicObjectParity()
    {
        var scene = Document ?? throw new InvalidOperationException("Polymorphic verification requires the actual scene.");
        var history = scene.SessionHistory ?? throw new InvalidOperationException("Polymorphic verification requires native history.");
        if (!Environment.GetCommandLineArgs().Contains("--ui-test") || scene.IsDirty
            || !System.IO.Path.GetFullPath(scene.Path).StartsWith(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Stride-ImGui-UI") + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Polymorphic verification requires a clean private fixture.");
        var selection = scene.SelectedIds.ToArray(); var primary = scene.Selected?.Id ?? Guid.Empty;
        scene.Save(); var baseline = File.ReadAllBytes(scene.Path); var boundary = history.Stack.NextUndoTransaction;
        var graph = scene.HierarchyGraph; var nativeAsset = scene.Asset;
        var first = nativeAsset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Sphere").Entity;
        var second = nativeAsset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Imported cube").Entity;
        var script = EntityHierarchyDocument.RegisteredComponentTypes.Single(type => type.FullName == "EditorParity.InspectorTypes");
        var a = first.Components.FirstOrDefault(component => component.GetType() == script) ?? scene.AddComponent(first, script);
        var b = second.Components.FirstOrDefault(component => component.GetType() == script) ?? scene.AddComponent(second, script);
        var number = script.Assembly.GetType("EditorParity.InspectorOwnedNumber", true)!;
        var text = script.Assembly.GetType("EditorParity.InspectorOwnedText", true)!;
        CommonComponentProperty Property()
        {
            SelectCompositeHierarchyEntity(scene, first, false); SelectCompositeHierarchyEntity(scene, second, true);
            return ReadAggregateProperties().Single(property => property.Fields.Count == 2 && property.Fields.All(field => field.Name == "OwnedValue"));
        }
        object?[] Values() => Property().Fields.Select(field => field.Node.Retrieve()).ToArray();
        Guid[] Ids() => Values().Select(value => ((IIdentifiable)value!).Id).ToArray();
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
        void Change(Type? type, string title)
        { var property = Property(); ApplyAggregateMutation(property, field => scene.PrepareAggregateObjectChange(field, type), title); }
        Require(Values().All(value => value == null), "Polymorphic fixture must initially have nullable owned values.");
        Change(number, "Create aggregate native owned objects");
        var created = Values(); var createdIds = Ids(); var createdTransaction = history.Stack.NextUndoTransaction;
        Require(created.All(value => value?.GetType() == number) && !ReferenceEquals(created[0], created[1]) && createdIds.Distinct().Count() == 2,
            "Actual object factory failed independent typed owned object creation.");
        Change(number, "No-op aggregate native object type");
        Require(ReferenceEquals(createdTransaction, history.Stack.NextUndoTransaction) && createdIds.SequenceEqual(Ids()), "Same-type object choice added history or replaced native identity.");
        Change(text, "Replace aggregate native owned objects");
        var replaced = Values(); var replacementIds = Ids(); var replacementTransaction = history.Stack.NextUndoTransaction;
        Require(replaced.All(value => value?.GetType() == text) && replacementIds.Distinct().Count() == 2 && !replacementIds.Intersect(createdIds).Any(),
            "Concrete type replacement retained old identity or shared mutable objects.");
        history.Undo(); Require(createdIds.SequenceEqual(Ids()), "Replacement Undo lost native object identities.");
        history.Redo(); Require(replacementIds.SequenceEqual(Ids()) && ReferenceEquals(replacementTransaction, history.Stack.NextUndoTransaction), "Replacement Redo lost native identity or transaction.");
        var property = Property();
        var staleChanges = property.Fields.ToDictionary(field => field.Component, field => scene.PrepareAggregateObjectChange(field, number));
        var secondField = property.Fields.Single(field => ReferenceEquals(field.Component, b));
        scene.ApplyInheritedGraphMutation(scene.PrepareAggregateObjectChange(secondField, null), "Invalidate actual second owned object target");
        property = Property(); var beforeStale = scene.CaptureAssemblyReload().Yaml; var stalePosition = history.Stack.NextUndoTransaction; var disk = File.ReadAllBytes(scene.Path);
        var firstBefore = property.Fields.Single(field => ReferenceEquals(field.Component, a)).Node.Retrieve();
        var rejected = false;
        try { ApplyAggregateMutation(property, field => staleChanges[field.Component], "Reject stale polymorphic aggregate target"); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected && beforeStale.AsSpan().SequenceEqual(scene.CaptureAssemblyReload().Yaml) && ReferenceEquals(stalePosition, history.Stack.NextUndoTransaction)
            && ReferenceEquals(firstBefore, Property().Fields.Single(field => ReferenceEquals(field.Component, a)).Node.Retrieve()) && disk.AsSpan().SequenceEqual(File.ReadAllBytes(scene.Path)),
            "Stale second-owner object replacement failed atomic first-owner restoration.");
        history.Undo(); Require(replacementIds.SequenceEqual(Ids()), "Undo after stale target rejection lost owned objects.");
        scene.Save();
        var loaded = AssetFileSerializer.Load<SceneAsset>(scene.Path).Asset;
        foreach (var entity in new[] { first, second })
        {
            var savedComponent = loaded.Hierarchy.Parts[entity.Id].Entity.Components.Single(component => component.GetType() == script);
            var savedValue = script.GetProperty("OwnedValue")!.GetValue(savedComponent)!;
            var current = Property().Fields.Single(field => field.Component.Entity == entity).Node.Retrieve()!;
            Require(savedValue.GetType() == text && ((IIdentifiable)savedValue).Id == ((IIdentifiable)current).Id
                && Equals(text.GetProperty("Text")!.GetValue(savedValue), "Ω 日本語 🚀"), "Independent native polymorphic persistence lost type, identity or Unicode value.");
        }
        Change(null, "Clear aggregate native owned objects");
        Require(Values().All(value => value == null), "Actual owned object clear failed.");
        history.Undo(); Require(replacementIds.SequenceEqual(Ids()), "Clear Undo lost native owned object identities.");
        history.Redo(); Require(Values().All(value => value == null), "Clear Redo failed.");
        var budget = 128;
        while (!ReferenceEquals(history.Stack.NextUndoTransaction, boundary))
        { if (--budget == 0 || !history.Stack.CanRollback) throw new InvalidOperationException("Polymorphic fixture history cleanup failed."); history.Undo(); }
        scene.Save(); scene.SelectRange(selection, primary);
        Require(baseline.AsSpan().SequenceEqual(File.ReadAllBytes(scene.Path)) && ReferenceEquals(graph, scene.HierarchyGraph) && ReferenceEquals(nativeAsset, scene.Asset),
            "Polymorphic fixture did not restore exact canonical baseline.");
        return new { schema = 1, nativeFactoryCreate = true, independentObjectIds = true, concreteReplacement = true, sameTypeNoop = true,
            undoRedoIdentity = true, staleSecondAtomicRejection = true, nativePersistence = true, nullableClear = true, baselineRestored = true,
            coverage = "Native object factory and canonical aggregate Quantum journals over two real component owners in one scene; no UI type picker, cross-document or external identifiable-reference cleanup claim." };
    }
}
