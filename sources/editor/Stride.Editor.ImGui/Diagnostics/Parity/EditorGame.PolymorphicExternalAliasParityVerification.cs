using Stride.Assets.Entities;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    internal object VerifyPolymorphicExternalAliasParity()
    {
        var scene = Document ?? throw new InvalidOperationException("External alias verification requires the actual scene.");
        var history = scene.SessionHistory ?? throw new InvalidOperationException("External alias verification requires native history.");
        if (!Environment.GetCommandLineArgs().Contains("--ui-test") || scene.IsDirty
            || !System.IO.Path.GetFullPath(scene.Path).StartsWith(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Stride-ImGui-UI") + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("External alias verification requires a clean private fixture.");
        var selection = scene.SelectedIds.ToArray(); var primary = scene.Selected?.Id ?? Guid.Empty;
        scene.Save(); var baseline = File.ReadAllBytes(scene.Path); var boundary = history.Stack.NextUndoTransaction;
        var graph = scene.HierarchyGraph; var asset = scene.Asset;
        var first = asset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Sphere").Entity;
        var target = asset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Imported cube").Entity;
        var script = EntityHierarchyDocument.RegisteredComponentTypes.Single(type => type.FullName == "EditorParity.InspectorTypes");
        var a = first.Components.FirstOrDefault(component => component.GetType() == script) ?? scene.AddComponent(first, script);
        var b = target.Components.FirstOrDefault(component => component.GetType() == script) ?? scene.AddComponent(target, script);
        var number = script.Assembly.GetType("EditorParity.InspectorOwnedNumber", true)!;
        var text = script.Assembly.GetType("EditorParity.InspectorOwnedText", true)!;
        CommonComponentProperty Property()
        {
            SelectCompositeHierarchyEntity(scene, first, false); SelectCompositeHierarchyEntity(scene, target, true);
            return ReadAggregateProperties().Single(property => property.Fields.Count == 2 && property.Fields.All(field => field.Name == "OwnedValue"));
        }
        object Value(EntityComponent component) => scene.GetComponentFields(component).Single(field => field.Name == "OwnedValue").Node.Retrieve()!;
        void Change(Type? type, string title)
        { var property = Property(); ApplyAggregateMutation(property, field => scene.PrepareAggregateObjectChange(field, type), title); }
        void Set(EntityComponent component, string name, object value)
        {
            SelectCompositeHierarchyEntity(scene, component.Entity!, false);
            var fields = scene.GetComponentFields(component);
            var field = name.StartsWith("External", StringComparison.Ordinal)
                ? fields.Single(field => field.Name == "OwnedValue").Children.Single(field => field.Node.Name == name)
                : fields.Single(field => field.Name == name);
            if (!field.IsSceneReference || !graph.Definition.IsMemberTargetObjectReference(field.Node, value))
                throw new InvalidOperationException("The fixture member is not a genuine native scene reference.");
            scene.UpdateComponent(field, value); scene.EndComponentEdit();
        }
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
        Change(number, "Arrange real owned objects with native external aliases");
        foreach (var component in new[] { a, b })
        { Set(component, "ExternalEntity", target); Set(component, "ExternalTransform", target.Transform); }
        Set(b, "TargetEntity", target); Set(b, "TargetTransform", target.Transform);
        var originalValues = new[] { Value(a), Value(b) }; var ids = originalValues.Select(value => ((IIdentifiable)value).Id).ToArray();
        Require(!ReferenceEquals(originalValues[0], originalValues[1]) && ids.Distinct().Count() == 2, "The alias fixture does not have independent owned objects.");
        void CheckTargets()
        {
            Require(ReferenceEquals(asset.Hierarchy.Parts[target.Id].Entity, target) && target.Components.Contains(target.Transform), "Owned object lifecycle removed the canonical external target.");
            Require(ReferenceEquals(script.GetProperty("TargetEntity")!.GetValue(b), target)
                && ReferenceEquals(script.GetProperty("TargetTransform")!.GetValue(b), target.Transform), "Owned object lifecycle cleared another owner's actual references.");
        }
        void CheckOriginalAliases()
        {
            var values = new[] { Value(a), Value(b) };
            Require(ids.SequenceEqual(values.Select(value => ((IIdentifiable)value).Id)) && values.Zip(originalValues).All(pair => ReferenceEquals(pair.First, pair.Second)), "Native replay lost exact owned object identities.");
            foreach (var value in values)
                Require(ReferenceEquals(number.GetProperty("ExternalEntity")!.GetValue(value), target)
                    && ReferenceEquals(number.GetProperty("ExternalTransform")!.GetValue(value), target.Transform), "Native replay lost outgoing entity/component alias identity.");
            CheckTargets();
        }
        CheckOriginalAliases();
        Change(text, "Replace owned objects without deleting external targets"); CheckTargets();
        history.Undo(); CheckOriginalAliases(); history.Redo(); CheckTargets(); history.Undo(); CheckOriginalAliases();
        Change(null, "Clear owned objects without deleting external targets"); CheckTargets();
        Require(Value(a) == null && Value(b) == null, "Native nullable clear did not remove owned values.");
        history.Undo(); CheckOriginalAliases(); history.Redo(); CheckTargets(); history.Undo(); CheckOriginalAliases();
        scene.Save();
        var loaded = AssetFileSerializer.Load<SceneAsset>(scene.Path).Asset;
        var savedTarget = loaded.Hierarchy.Parts[target.Id].Entity;
        foreach (var entity in new[] { first, target })
        {
            var savedScript = loaded.Hierarchy.Parts[entity.Id].Entity.Components.Single(component => component.GetType() == script);
            var savedValue = script.GetProperty("OwnedValue")!.GetValue(savedScript)!;
            Require(savedValue.GetType() == number && ((IIdentifiable)savedValue).Id == ((IIdentifiable)Value(entity == first ? a : b)).Id
                && ReferenceEquals(number.GetProperty("ExternalEntity")!.GetValue(savedValue), savedTarget)
                && ReferenceEquals(number.GetProperty("ExternalTransform")!.GetValue(savedValue), savedTarget.Transform), "Independent native YAML reload lost actual external aliases or owned identity.");
        }
        var savedOther = savedTarget.Components.Single(component => component.GetType() == script);
        Require(ReferenceEquals(script.GetProperty("TargetEntity")!.GetValue(savedOther), savedTarget)
            && ReferenceEquals(script.GetProperty("TargetTransform")!.GetValue(savedOther), savedTarget.Transform), "Native reload lost unrelated owner's references.");
        var budget = 128;
        while (!ReferenceEquals(history.Stack.NextUndoTransaction, boundary))
        { if (--budget == 0 || !history.Stack.CanRollback) throw new InvalidOperationException("External alias fixture cleanup failed."); history.Undo(); }
        scene.Save(); scene.SelectRange(selection, primary);
        Require(baseline.AsSpan().SequenceEqual(File.ReadAllBytes(scene.Path)) && ReferenceEquals(graph, scene.HierarchyGraph) && ReferenceEquals(asset, scene.Asset), "External alias fixture did not restore exact canonical baseline.");
        return new { schema = 1, nativeReferenceClassification = true, independentOwnedObjects = true, replacementPreservesTargets = true,
            clearPreservesTargets = true, otherOwnerReferencesPreserved = true, undoRedoExactAliases = true, nativeYamlAliases = true, baselineRestored = true,
            coverage = "Two owned polymorphic objects with genuine Entity/Transform outgoing references; production aggregate lifecycle and native replay/persistence. No arbitrary custom incoming-reference cleanup or widget/cross-document claim." };
    }
}
