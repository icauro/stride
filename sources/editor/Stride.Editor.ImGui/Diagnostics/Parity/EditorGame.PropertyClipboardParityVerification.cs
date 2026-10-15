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
    internal object VerifyPropertyClipboardParity()
    {
        var scene = Document ?? throw new InvalidOperationException("Clipboard verification requires the actual scene.");
        var history = scene.SessionHistory ?? throw new InvalidOperationException("Clipboard verification requires native history.");
        if (!Environment.GetCommandLineArgs().Contains("--ui-test") || scene.IsDirty
            || !System.IO.Path.GetFullPath(scene.Path).StartsWith(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Stride-ImGui-UI") + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Clipboard verification requires a clean private fixture.");
        var selection = scene.SelectedIds.ToArray(); var primary = scene.Selected?.Id ?? Guid.Empty;
        scene.Save();
        var baseline = File.ReadAllBytes(scene.Path); var boundary = history.Stack.NextUndoTransaction;
        var graph = scene.HierarchyGraph; var asset = scene.Asset;
        var first = asset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Sphere").Entity;
        var second = asset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Imported cube").Entity;
        var type = EntityHierarchyDocument.RegisteredComponentTypes.Single(value => value.FullName == "EditorParity.InspectorTypes");
        var a = first.Components.FirstOrDefault(value => value.GetType() == type) ?? scene.AddComponent(first, type);
        var b = second.Components.FirstOrDefault(value => value.GetType() == type) ?? scene.AddComponent(second, type);
        ComponentField Field(EntityComponent component, string name) => scene.GetComponentFields(component).Single(field => field.Name == name);
        void Set(EntityComponent component, string name, object value)
        { SelectCompositeHierarchyEntity(scene, component.Entity!, false); scene.UpdateComponent(Field(component, name), value); scene.EndComponentEdit(); }
        byte[] MemoryGraph() => scene.CaptureAssemblyReload().Yaml;
        string Copy(EntityComponent component, string name)
        {
            var before = MemoryGraph(); var position = history.Stack.NextUndoTransaction; var disk = File.ReadAllBytes(scene.Path);
            var text = scene.CopyProperty(Field(component, name));
            if (text.Length == 0 || !before.AsSpan().SequenceEqual(MemoryGraph()) || !ReferenceEquals(position, history.Stack.NextUndoTransaction)
                || !disk.AsSpan().SequenceEqual(File.ReadAllBytes(scene.Path))) throw new InvalidOperationException("Native property copy mutated its owner.");
            return text;
        }
        void Reject(ComponentField[] fields, string text)
        {
            var before = MemoryGraph(); var position = history.Stack.NextUndoTransaction; var disk = File.ReadAllBytes(scene.Path);
            var rejected = false;
            try { scene.PasteProperties(fields, text, true); }
            catch (InvalidOperationException) { rejected = true; }
            if (!rejected || !before.AsSpan().SequenceEqual(MemoryGraph()) || !ReferenceEquals(position, history.Stack.NextUndoTransaction)
                || !disk.AsSpan().SequenceEqual(File.ReadAllBytes(scene.Path))) throw new InvalidOperationException("Rejected clipboard destination mutated graph, history or disk.");
        }
        Set(a, "Counter64", 987UL);
        var scalar = Copy(a, "Counter64");
        var beforePaste = history.Stack.NextUndoTransaction;
        scene.PasteProperties([Field(b, "Counter64")], scalar, true);
        var pasted = history.Stack.NextUndoTransaction;
        if (ReferenceEquals(pasted, beforePaste) || !Equals(Field(b, "Counter64").Node.Retrieve(), 987UL)) throw new InvalidOperationException("Typed scalar paste failed.");
        history.Undo();
        if (!ReferenceEquals(history.Stack.NextUndoTransaction, beforePaste) || Equals(Field(b, "Counter64").Node.Retrieve(), 987UL)) throw new InvalidOperationException("Clipboard Undo failed.");
        history.Redo();
        if (!ReferenceEquals(history.Stack.NextUndoTransaction, pasted) || !Equals(Field(b, "Counter64").Node.Retrieve(), 987UL)) throw new InvalidOperationException("Clipboard Redo failed.");
        SelectCompositeHierarchyEntity(scene, a.Entity!, false);
        scene.AddDictionaryItem(Field(a, "NumberedLabels"), "4", null);
        var labelItem = scene.GetCollectionItems(Field(a, "NumberedLabels")).Single(item => Equals(item.Index.Value, 4));
        scene.UpdateCollectionItem(labelItem, "Ω 日本語 🚀");
        scene.EndCollectionEdit();
        var collection = Copy(a, "NumberedLabels");
        var collectionPosition = history.Stack.NextUndoTransaction;
        var destinationBefore = Field(b, "NumberedLabels");
        var destinationBeforeValues = ((IDictionary)destinationBefore.Node.Retrieve()!).Keys.Cast<object>().Select(key => new { key, value = ((IDictionary)destinationBefore.Node.Retrieve()!)[key] }).ToArray();
        var canPasteCollection = scene.CanPasteProperties([destinationBefore], collection);
        scene.PasteProperties([Field(b, "NumberedLabels")], collection, true);
        var sourceMap = (IDictionary)Field(a, "NumberedLabels").Node.Retrieve()!;
        var targetMap = (IDictionary)Field(b, "NumberedLabels").Node.Retrieve()!;
        if (ReferenceEquals(sourceMap, targetMap) || !Equals(targetMap[4], "Ω 日本語 🚀"))
        {
            var receipt = new
            {
                payload = collection, canPasteCollection, replace = true,
                aliases = ReferenceEquals(sourceMap, targetMap), sourceType = sourceMap.GetType().AssemblyQualifiedName, targetType = targetMap.GetType().AssemblyQualifiedName,
                source = sourceMap.Keys.Cast<object>().Select(key => new { key, value = sourceMap[key] }).ToArray(),
                target = targetMap.Keys.Cast<object>().Select(key => new { key, value = targetMap[key] }).ToArray(), destinationBeforeValues,
                historyChanged = !ReferenceEquals(collectionPosition, history.Stack.NextUndoTransaction),
                selectedSource = scene.SelectedIds.Contains(first.Id), selectedTarget = scene.SelectedIds.Contains(second.Id),
                targetFieldKnown = scene.IsKnownComponentField(Field(b, "NumberedLabels")),
                targetFieldEditable = scene.CanEditHierarchyField(Field(b, "NumberedLabels")),
                targetCollectionEditable = scene.CanEditHierarchyNode(Field(b, "NumberedLabels").Node.Target!),
                targetCanAdd = scene.CanAddDictionaryKeys(Field(b, "NumberedLabels")),
                targetNativeHasAdd = NativePropertyClipboard.HasAdd(Field(b, "NumberedLabels").Node.Target!),
                targetDeclaredType = Field(b, "NumberedLabels").DeclaredType.AssemblyQualifiedName,
                sourceKey4 = sourceMap[4], targetKey4 = targetMap[4], expected = "Ω 日本語 🚀"
            };
            var root = Environment.GetEnvironmentVariable("STRIDE_EDITOR_TEST_ARTIFACTS")
                ?? throw new InvalidOperationException("Clipboard diagnostic artifacts are not isolated.");
            File.WriteAllText(System.IO.Path.Combine(root, "property-clipboard-collection-failure.json"), System.Text.Json.JsonSerializer.Serialize(receipt));
            throw new InvalidOperationException("Unicode primitive collection was not independently cloned; exact native payload/value/authorability receipt retained.");
        }
        Set(a, "TargetEntity", first);
        var reference = Copy(a, "TargetEntity");
        scene.PasteProperties([Field(b, "TargetEntity")], reference, true);
        if (!ReferenceEquals(Field(b, "TargetEntity").Node.Retrieve(), first)) throw new InvalidOperationException("Clipboard lost actual owned entity-reference identity.");
        Reject([Field(b, "Counter64")], "!not-a-native-property-payload");
        var stale = Field(b, "Counter64");
        scene.RemoveComponent(b);
        Reject([stale], scalar);
        history.Undo();
        b = second.Components.Single(value => value.GetType() == type);
        scene.Save();
        var loaded = AssetFileSerializer.Load<SceneAsset>(scene.Path).Asset;
        var saved = loaded.Hierarchy.Parts[second.Id].Entity.Components.Single(value => value.GetType() == type);
        if (!Equals(type.GetProperty("Counter64")!.GetValue(saved), 987UL)
            || !Equals(((IDictionary)type.GetProperty("NumberedLabels")!.GetValue(saved)!)[4], "Ω 日本語 🚀")
            || !ReferenceEquals(type.GetProperty("TargetEntity")!.GetValue(saved), loaded.Hierarchy.Parts[first.Id].Entity))
            throw new InvalidOperationException("Independent native clipboard persistence lost typed values or reference aliases.");
        SelectCompositeHierarchyEntity(scene, first, false);
        var extraction = scene.PreparePrefabExtraction();
        var nativeScene = workspace!.Session.FindAsset(asset.Id)!;
        var item = AssetItem.CreateForPackage(nativeScene.Package, new UFile("PropertyClipboardBase"), extraction.Prefab);
        item.SourceFolder = nativeScene.SourceFolder; extraction.Metadata.CopyInto(item.YamlMetadata);
        var path = item.FullPath.ToOSPath();
        if (File.Exists(path)) throw new InvalidOperationException("Clipboard prefab fixture is occupied.");
        CreatePreparedAsset(nativeScene.Package, item, path, "Arrange property clipboard provenance");
        var state = authoring!.Find(item.Id)!; var prefab = (PrefabDocument)state.Document;
        using var lease = state.AcquireView();
        var foreignEntity = prefab.Asset.Hierarchy.RootParts.Single();
        var foreignScript = foreignEntity.Components.Single(value => value.GetType() == type);
        var foreignField = prefab.GetComponentFields(foreignScript).Single(field => field.Name == "TargetEntity");
        var foreignPayload = prefab.CopyProperty(foreignField);
        var foreignPosition = history.Stack.NextUndoTransaction;
        var beforeForeign = MemoryGraph(); var beforeForeignDisk = File.ReadAllBytes(scene.Path);
        var foreignRejected = false;
        try { scene.PasteProperties([Field(b, "TargetEntity")], foreignPayload, true); }
        catch (InvalidOperationException) { foreignRejected = true; }
        var pastedReference = Field(b, "TargetEntity").Node.Retrieve() as Entity;
        var foreignReceipt = new
        {
            payload = foreignPayload, sourceAssetId = prefab.Asset.Id, destinationAssetId = scene.Asset.Id,
            sourceEntityId = foreignEntity.Id, canonicalFirstId = first.Id, destinationEntityId = pastedReference?.Id,
            aliasesCanonicalFirst = ReferenceEquals(pastedReference, first), aliasesForeignEntity = ReferenceEquals(pastedReference, foreignEntity),
            foreignSourceAliasesOwnedRoot = ReferenceEquals(foreignField.Node.Retrieve(), foreignEntity),
            foreignIdAbsentFromDestination = !scene.Asset.Hierarchy.Parts.ContainsKey(foreignEntity.Id),
            destinationCanonical = pastedReference != null && scene.Asset.Hierarchy.Parts.TryGetValue(pastedReference.Id, out var pastedPart) && ReferenceEquals(pastedPart.Entity, pastedReference),
            foreignRejected, historyChanged = !ReferenceEquals(foreignPosition, history.Stack.NextUndoTransaction),
            graphUnchanged = beforeForeign.AsSpan().SequenceEqual(MemoryGraph()), diskUnchanged = beforeForeignDisk.AsSpan().SequenceEqual(File.ReadAllBytes(scene.Path))
        };
        var foreignArtifacts = Environment.GetEnvironmentVariable("STRIDE_EDITOR_TEST_ARTIFACTS")
            ?? throw new InvalidOperationException("Clipboard diagnostic artifacts are not isolated.");
        File.WriteAllText(System.IO.Path.Combine(foreignArtifacts, "property-clipboard-foreign-reference.json"), System.Text.Json.JsonSerializer.Serialize(foreignReceipt));
        if (!foreignReceipt.foreignSourceAliasesOwnedRoot || !foreignReceipt.foreignIdAbsentFromDestination)
            throw new InvalidOperationException("Clipboard provenance fixture does not contain a real foreign reference.");
        if (!foreignRejected || foreignReceipt.historyChanged || !foreignReceipt.graphUnchanged || !foreignReceipt.diskUnchanged)
            throw new InvalidOperationException("Foreign clipboard rejection did not preserve native graph, history and disk.");
        if (!ReferenceEquals(Field(b, "TargetEntity").Node.Retrieve(), first)) throw new InvalidOperationException("Rejected foreign clipboard reference lost canonical destination identity.");
        var sealedBase = prefab.GetComponentFields(foreignScript).Single(field => field.Name == "Counter64");
        prefab.ApplyInheritedGraphMutation(() => ((IAssetMemberNode)sealedBase.Node).RestoreContentOverride(OverrideType.Sealed), "Arrange sealed property clipboard destination");
        prefab.Save();
        var instance = scene.InsertPrefab(prefab, "PropertyClipboardBase", null, "", -1, false, null).Roots.Single();
        var instanceScript = instance.Components.Single(value => value.GetType() == type);
        var sealedDestination = Field(instanceScript, "Counter64");
        if (sealedDestination.Presentation?.IsReadOnly(sealedDestination) != true) throw new InvalidOperationException("Clipboard fixture lacks actual inherited sealing.");
        Reject([Field(b, "Counter64"), sealedDestination], scalar);
        var budget = 128;
        while (!ReferenceEquals(history.Stack.NextUndoTransaction, boundary))
        { if (--budget == 0 || !history.Stack.CanRollback) throw new InvalidOperationException("Clipboard fixture history cleanup failed."); history.Undo(); }
        scene.Save(); scene.SelectRange(selection, primary);
        if (!baseline.AsSpan().SequenceEqual(File.ReadAllBytes(scene.Path)) || !ReferenceEquals(graph, scene.HierarchyGraph) || !ReferenceEquals(asset, scene.Asset)
            || state.IsPublished || workspace.Session.FindAsset(item.Id) != null || File.Exists(path)) throw new InvalidOperationException("Clipboard fixture failed exact baseline and owner retirement.");
        return new { schema = 1, scalarTyped = true, unicodeCollectionCloned = true, ownedReferenceIdentity = true, copyReadOnly = true,
            incompatibleRejected = true, staleComponentRejected = true, sealedMixedRejected = true, foreignReferenceRejected = true,
            undoRedo = true, nativeSavedRoundtrip = true, baselineRestored = true,
            coverage = "Native in-memory WPF clipboard format and canonical mutation services; stale removed-component certificate, not swapped-generation or widget/OS clipboard coverage." };
    }
}
