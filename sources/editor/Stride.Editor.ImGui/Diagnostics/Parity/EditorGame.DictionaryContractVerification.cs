using System.Collections;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.IO;
using Stride.Core.Reflection;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private IEnumerator<object?>? dictionaryContract;
    internal void StartDictionaryContractVerification()
    {
        if (dictionaryContract != null) throw new InvalidOperationException("Dictionary verification is already pending.");
        dictionaryContract = RunDictionaryContractVerification().GetEnumerator();
    }
    internal object? StepDictionaryContractVerification()
    {
        var run = dictionaryContract ?? throw new InvalidOperationException("Dictionary verification is not pending.");
        try
        {
            if (!run.MoveNext()) throw new InvalidOperationException("Dictionary verification ended without evidence.");
            var result = run.Current;
            if (result != null) CancelDictionaryContractVerification();
            return result;
        }
        catch { CancelDictionaryContractVerification(); throw; }
    }
    internal void CancelDictionaryContractVerification() { var run = dictionaryContract; dictionaryContract = null; run?.Dispose(); }
    private IEnumerable<object?> RunDictionaryContractVerification()
    {
        if (!args.Contains("--ui-test")) throw new InvalidOperationException("Dictionary verification requires a private UI fixture.");
        var scene = Document ?? throw new InvalidOperationException("No canonical scene is open.");
        if (!scene.Path.StartsWith(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Dictionary verification cannot save an original project.");
        var history = sessionHistory!;
        if (scene.IsDirty) throw new InvalidOperationException("Dictionary contract requires the current scene's saved baseline.");
        var baselineUndo = history.Stack.NextUndoTransaction;
        var initialTransactions = history.Stack.RetrieveAllTransactions().ToArray();
        var originalInputSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(scene.Path)));
        var canonicalAsset = scene.Asset;
        var canonicalGraph = scene.HierarchyGraph;
        scene.Save(); // Fixture arrangement: materialize actual native serializer defaults before byte assertions.
        if (!ReferenceEquals(history.Stack.NextUndoTransaction, baselineUndo)
            || !initialTransactions.SequenceEqual(history.Stack.RetrieveAllTransactions()) || scene.IsDirty
            || !ReferenceEquals(scene.Asset, canonicalAsset) || !ReferenceEquals(scene.HierarchyGraph, canonicalGraph))
            throw new InvalidOperationException("Native baseline serialization changed canonical graph or authored history.");
        var baselineBytes = File.ReadAllBytes(scene.Path);
        var canonicalBaselineSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(baselineBytes));
        var entity = scene.Asset.Hierarchy.Parts.Values.First(part => part.Entity.Name == "Sphere").Entity;
        scene.Selected = entity;
        var type = EntityHierarchyDocument.RegisteredComponentTypes.Single(component => component.FullName == "EditorParity.InspectorTypes");
        var component = entity.Components.FirstOrDefault(value => value.GetType() == type) ?? scene.AddComponent(entity, type);
        var evidence = new List<object>();
        foreach (var name in new[] { "NamedCounters", "NumberedLabels" })
        {
            ComponentField Field() => scene.GetComponentFields(component).Single(field => field.Name == name);
            var keys = name == "NamedCounters" ? new[] { "first Ω", "second 日本語", "renamed Δ" } : new[] { "101", "202", "303" };
            object Key(string text) => scene.ParseDictionaryKey(Field(), text);
            IDictionary Values() => (IDictionary)Field().Node.Retrieve()!;
            EditorPresenterTarget Target(string? key) => scene.AcquireHierarchyItemTarget((IAssetObjectNode)Field().Node.Target!,
                key == null ? NodeIndex.Empty : new NodeIndex(Key(key)), key == null)
                ?? throw new InvalidOperationException("The actual registered dictionary presenter is unavailable.");
            var nativeCommandUses = new List<string>();
            void Start(string commandName, string? key, object? parameter)
            {
                var field = Field();
                var context = scene.GetRootHierarchyCollection(field)
                    ?? throw new InvalidOperationException("The actual dictionary collection context is unavailable.");
                var item = key == null ? null : scene.GetCollectionItems(field).Single(item => Equals(item.Index.Value, Key(key)));
                if (commandName == "RemoveItem")
                {
                    scene.RemoveCollectionItem(item!);
                    return;
                }
                if (commandName == "AddPrimitiveKey" && !scene.CanAddNestedHierarchyKeys(context)
                    || commandName == "RenameStringKey" && (!scene.CanEditDictionaryKeys(field) || !scene.CanEditHierarchyItem(item!.Collection, item.Index)))
                    throw new InvalidOperationException("The actual dictionary operation is ineligible.");
                var target = Target(key);
                var command = target.Presenter.Commands.SingleOrDefault(command => command.Name == commandName);
                if (command != null) nativeCommandUses.Add(commandName);
                RunHierarchyFactoryCommand([scene], [target], commandName, parameter, () =>
                {
                    if (commandName == "AddPrimitiveKey") scene.AddHierarchyDictionaryDefaultItem(context, parameter);
                    else scene.RenameDictionaryKey(item!, (string)parameter!);
                });
            }
            foreach (var key in keys.Take(2))
            {
                Start("AddPrimitiveKey", null, key);
                do { yield return null; } while (IsEditorExtensionCommandRunning);
                if (!Values().Contains(Key(key))) throw new InvalidOperationException("Native dictionary insert did not publish its typed key.");
            }
            var beforeCollision = history.Stack.RetrieveAllTransactions().Count();
            Start("RenameStringKey", keys[0], keys[1]);
            do { yield return null; } while (IsEditorExtensionCommandRunning);
            if (Values().Count != 2 || history.Stack.RetrieveAllTransactions().Count() != beforeCollision)
                throw new InvalidOperationException("A native dictionary collision changed its graph or history.");
            Start("RenameStringKey", keys[0], keys[0]);
            do { yield return null; } while (IsEditorExtensionCommandRunning);
            if (history.Stack.RetrieveAllTransactions().Count() != beforeCollision) throw new InvalidOperationException("Same-key rename added native history.");
            var value = Values()[Key(keys[0])];
            Start("RenameStringKey", keys[0], keys[2]);
            do { yield return null; } while (IsEditorExtensionCommandRunning);
            if (Values().Contains(Key(keys[0])) || !Equals(Values()[Key(keys[2])], value)) throw new InvalidOperationException("Native dictionary rename lost its typed value.");
            history.Undo(); if (!Values().Contains(Key(keys[0]))) throw new InvalidOperationException("Dictionary rename Undo failed.");
            history.Redo(); if (!Values().Contains(Key(keys[2]))) throw new InvalidOperationException("Dictionary rename Redo failed.");
            Start("RemoveItem", keys[1], null);
            do { yield return null; } while (IsEditorExtensionCommandRunning);
            if (Values().Count != 1) throw new InvalidOperationException("Dictionary remove failed.");
            history.Undo(); if (!Values().Contains(Key(keys[1]))) throw new InvalidOperationException("Dictionary remove Undo failed.");
            history.Redo();
            scene.Save();
            var reloaded = AssetFileSerializer.Load<Stride.Assets.Entities.SceneAsset>(scene.Path).Asset;
            var diskComponent = reloaded.Hierarchy.Parts[entity.Id].Entity.Components.Single(value => value.GetType() == type);
            var diskValues = (IDictionary)type.GetProperty(name)!.GetValue(diskComponent)!;
            if (diskValues.Count != 1 || !diskValues.Contains(Key(keys[2])) || !Equals(diskValues[Key(keys[2])], value))
                throw new InvalidOperationException("Independent native dictionary roundtrip lost its key/value.");
            evidence.Add(new { property = name, keyType = Key(keys[2]).GetType().FullName, collisionNoop = true, undoRedo = true, savedRoundtrip = true,
                registeredCommandsInvoked = nativeCommandUses, route = "Production add/rename provider-or-canonical-fallback dispatcher; canonical Remove button operation" });
        }
        scene.Selected = entity;
        var extraction = scene.PreparePrefabExtraction();
        var prefabPath = Path.Combine(Path.GetDirectoryName(scene.Path)!, "DictionaryContractBase.sdprefab");
        if (File.Exists(prefabPath)) throw new InvalidOperationException("Dictionary prefab fixture path is occupied.");
        var sceneItem = workspace!.Session.FindAsset(scene.Asset.Id)
            ?? throw new InvalidOperationException("Dictionary fixture has no actual scene package owner.");
        var prefabLocation = new UFile("DictionaryContractBase");
        var prefabItem = AssetItem.CreateForPackage(sceneItem.Package, prefabLocation, extraction.Prefab);
        prefabItem.SourceFolder = sceneItem.SourceFolder;
        extraction.Metadata.CopyInto(prefabItem.YamlMetadata);
        if (!SamePath(prefabItem.FullPath.ToOSPath(), prefabPath))
            throw new InvalidOperationException("Dictionary prefab fixture URL does not own its private package path.");
        CreatePreparedAsset(sceneItem.Package, prefabItem, prefabPath, "Arrange dictionary prefab fixture");
        var state = authoring!.Find(prefabItem.Id)
            ?? throw new InvalidOperationException("Dictionary prefab fixture was not published through its native package.");
        var prefab = state.Document as PrefabDocument
            ?? throw new InvalidOperationException("Dictionary prefab fixture has another canonical editor kind.");
        using var lease = state.AcquireView();
        var baseComponent = prefab.Asset.Hierarchy.RootParts.Single().Components.Single(value => value.GetType() == type);
        var baseField = prefab.GetComponentFields(baseComponent).Single(field => field.Name == "NamedCounters");
        prefab.ApplyInheritedGraphMutation(() => ((IAssetMemberNode)baseField.Node).RestoreContentOverride(OverrideType.Sealed), "Arrange sealed native dictionary");
        prefab.Save();
        var instance = scene.InsertPrefab(prefab, "DictionaryContractBase", null, "", -1, false, null).Roots.Single();
        scene.Selected = instance;
        var derivedComponent = instance.Components.Single(value => value.GetType() == type);
        var derivedField = scene.GetComponentFields(derivedComponent).Single(field => field.Name == "NamedCounters");
        var sealedBefore = history.Stack.RetrieveAllTransactions().ToArray();
        var sealedDisk = File.ReadAllBytes(scene.Path);
        var baseDisk = File.ReadAllBytes(prefabPath);
        var sealedText = System.Text.Json.JsonSerializer.Serialize(derivedField.Node.Retrieve());
        object? sealedPolicy = null;
        using (var target = scene.AcquireHierarchyItemTarget((IAssetObjectNode)derivedField.Node.Target!, NodeIndex.Empty, true)
            ?? throw new InvalidOperationException("The sealed dictionary native presenter is missing."))
        {
            var canAddKeys = scene.CanAddDictionaryKeys(derivedField);
            var canAddNestedKeys = scene.CanAddNestedHierarchyKeys(scene.GetRootHierarchyCollection(derivedField)!);
            var effectiveReadOnly = derivedField.Presentation?.IsReadOnly(derivedField) == true;
            var member = (IAssetMemberNode)derivedField.Node;
            sealedPolicy = new { canAddKeys, canAddNestedKeys, effectiveReadOnly, rawPresenterReadOnly = target.Presenter.IsReadOnly,
                actualBaseNode = member.BaseNode != null, baseOverride = (member.BaseNode as IAssetMemberNode)?.GetContentOverride().ToString(),
                nativeInheritedSeal = scene.HierarchyGraph.IsInheritedNodeSealed(member),
                canonicalBaseGeneration = state.Generation };
            if (canAddKeys || canAddNestedKeys || !effectiveReadOnly || member.BaseNode == null || !scene.HierarchyGraph.IsInheritedNodeSealed(member))
                throw new InvalidOperationException("The actual inherited sealed dictionary remains effectively editable: "
                    + System.Text.Json.JsonSerializer.Serialize(sealedPolicy));
        }
        var rejected = false;
        try
        {
            var target = scene.AcquireHierarchyItemTarget((IAssetObjectNode)derivedField.Node.Target!, NodeIndex.Empty, true)!;
            RunHierarchyFactoryCommand([scene], [target], "AddPrimitiveKey", "forbidden",
                () => scene.AddHierarchyDictionaryDefaultItem(scene.GetRootHierarchyCollection(derivedField)!, "forbidden"));
        }
        catch (InvalidOperationException) { rejected = true; }
        if (!rejected || !sealedBefore.SequenceEqual(history.Stack.RetrieveAllTransactions())
            || sealedText != System.Text.Json.JsonSerializer.Serialize(derivedField.Node.Retrieve())
            || !sealedDisk.AsSpan().SequenceEqual(File.ReadAllBytes(scene.Path)) || !baseDisk.AsSpan().SequenceEqual(File.ReadAllBytes(prefabPath)))
            throw new InvalidOperationException("Sealed dictionary rejection mutated native graph/history.");
        var replayBudget = 128;
        while (!ReferenceEquals(history.Stack.NextUndoTransaction, baselineUndo))
        {
            if (--replayBudget == 0 || !history.Stack.CanRollback) throw new InvalidOperationException("Dictionary fixture could not restore its original history position.");
            history.Undo();
        }
        scene.Save();
        if (!baselineBytes.AsSpan().SequenceEqual(File.ReadAllBytes(scene.Path)))
            throw new InvalidOperationException("Dictionary fixture did not restore its original native saved scene.");
        if (!ReferenceEquals(scene.Asset, canonicalAsset) || !ReferenceEquals(scene.HierarchyGraph, canonicalGraph)
            || !ReferenceEquals(history.Stack.NextUndoTransaction, baselineUndo))
            throw new InvalidOperationException("Dictionary fixture changed canonical graph identity or original history position.");
        if (state.IsPublished || workspace.Session.FindAsset(prefabItem.Id) != null || File.Exists(prefabPath)
            || scene.Asset.Hierarchy.Parts.Values.Any(part => part.Base?.BasePartAsset.Id == prefabItem.Id))
            throw new InvalidOperationException("Dictionary fixture retained an orphan native prefab owner, saved file or scene base reference.");
        yield return new { schema = 1, cases = evidence, sealedInheritedCommandIneligible = true, sealedMutationRejected = true, sealedPolicy,
            baselineArrangement = new { originalInputSha256, canonicalBaselineSha256, nativeSaveOnly = true,
                originalTransactionIds = initialTransactions.Select(transaction => transaction.Id).ToArray(),
                originalUndoTransactionId = baselineUndo?.Id, preparationHistoryUnchanged = true,
                finalCanonicalBytesRestored = true, canonicalGraphIdentityRetained = true },
            coverage = "Production dictionary provider-or-canonical-fallback routes, native presenter effective authorability, canonical Remove/history and independent saved roundtrip; provider command use is recorded, not invented. No physical input or widget click claim." };
    }
}
