using Hexa.NET.ImGui;
using Stride.Assets.Entities;
using Stride.Core.Assets;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class UiWorkflowTest
{
    private Guid referenceCamera, referenceOriginal, referenceScriptId, referenceSphere;
    private string? collectionSecondId, collectionAddedId;
    private Guid[] collectionOriginalTargets = [];
    private string[] collectionOriginalItems = [];
    private string? referenceClipboardBackup;
    private bool referenceClipboardCaptured;
    private ComponentField ReferenceField(string name) => game.Document!.GetComponentFields(
        game.Document.Selected!.Components.Single(component => component.GetType().Name == "InspectorTypes"))
        .Single(field => field.Name == name);
    private string ReferenceControl(string name) => "component/" + ReferenceField(name).Component.Id + "/" + name;
    private CollectionItem[] ReferenceItems() => game.Document!.GetCollectionItems(ReferenceField("TargetEntities")).ToArray();

    private bool PrepareReferenceScript()
    {
        foreach (var component in game.Document!.Selected!.Components.Where(component => component is not TransformComponent && component.GetType().Name != "InspectorTypes"))
        {
            var id = "component/" + component.Id + "/header";
            if (!game.Controls.Expanded.TryGetValue(id, out var expanded)) { ClickInspectorControl(id); return false; }
            if (expanded) { Click(id); return false; }
        }
        var script = ReferenceField("TargetEntity").Component;
        var header = "component/" + script.Id + "/header";
        if (!game.Controls.Expanded.TryGetValue(header, out var open) || !open)
        { ClickInspectorControl(header); return false; }
        return true;
    }

    private void AssertReferenceTarget(Guid id)
    {
        var field = ReferenceField("TargetEntity");
        var authored = field.Node.Retrieve();
        var native = game.ReadRuntimeComponentValueVerification(field);
        var runtimeMatches = game.RuntimeComponentMatches(field);
        Assert(authored is Entity target && target.Id == id && runtimeMatches,
            "Entity-reference picker must update actual authored/runtime target identity: expected=" + id
            + "; authored=" + (authored as Entity)?.Id + "; authoredType=" + authored?.GetType().FullName
            + "; owner=" + field.Component.Entity.Id + "; component=" + field.Component.Id
            + "; field=" + field.Name + "; runtimeTarget=" + (native as Entity)?.Id
            + "; runtimeType=" + native?.GetType().FullName + "; runtimeCanonicalMatch=" + runtimeMatches);
    }

    private void AssertCollectionTargets(IEnumerable<Guid> ids)
    {
        var field = ReferenceField("TargetEntities");
        var expected = ids.ToArray();
        var authored = ReferenceItems().Select(item => (item.Value as Entity)?.Id ?? Guid.Empty).ToArray();
        var raw = field.Node.Retrieve() is System.Collections.IEnumerable rawValues
            ? rawValues.Cast<object?>().Select(value => (value as Entity)?.Id ?? Guid.Empty).ToArray() : [];
        var native = game.ReadRuntimeComponentValueVerification(field);
        var runtime = native is System.Collections.IEnumerable values
            ? values.Cast<object?>().Select(value => (value as Entity)?.Id ?? Guid.Empty).ToArray() : [];
        var canonical = game.EntityReferencesMatch;
        Assert(authored.SequenceEqual(expected) && canonical,
            "Collection UI must preserve actual authored/runtime reference order and canonical native instances"
            + "; expected=" + string.Join(',', expected) + "; authored=" + string.Join(',', authored)
            + "; rawAuthored=" + string.Join(',', raw)
            + "; items=" + string.Join(',', ReferenceItems().Select(item => item.Id + "@" + item.Index + "=" + (item.Value as Entity)?.Id))
            + "; runtime=" + string.Join(',', runtime) + "; runtimeType=" + native?.GetType().FullName
            + "; canonicalReferences=" + canonical + "; field=" + field.Name
            + "; owner=" + field.Component.Entity.Id + "; component=" + field.Component.Id
            + "; referenceMismatches=" + System.Text.Json.JsonSerializer.Serialize(game.CaptureRuntimeReferenceMismatchesVerification())
            + "; ordering=" + System.Text.Json.JsonSerializer.Serialize(game.Document!.InspectCollectionOrdering(field)));
    }

    private void UpdateReferencesCollectionsScenario()
    {
        var io = Gui.GetIO();
        var collection = game.Document?.Selected?.Name == "Sphere" ? ReferenceControl("TargetEntities") + "/collection" : "";
        switch (stage)
        {
            case 11000: if (Click("entity/Sphere")) Next(); break;
            case 11001:
                if (game.Document?.Selected?.Name != "Sphere" || !PrepareReferenceScript()) break;
                referenceSphere = game.Document.Selected.Id;
                referenceScriptId = ReferenceField("TargetEntity").Component.Id;
                referenceOriginal = ((Entity)ReferenceField("TargetEntity").Node.Retrieve()!).Id;
                referenceCamera = game.Document.Asset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Camera").Entity.Id;
                if (ClickInspectorControl(ReferenceControl("TargetEntity") + "/reference")) Next(); break;
            case 11002: if (Click(ReferenceControl("TargetEntity") + "/reference/" + referenceCamera)) Next(); break;
            case 11003:
                AssertReferenceTarget(referenceCamera);
                if (Click("toolbar/undo")) Next(); break;
            case 11004:
                AssertReferenceTarget(referenceOriginal);
                if (Click("toolbar/redo")) Next(); break;
            case 11005:
                AssertReferenceTarget(referenceCamera);
                if (!referenceClipboardCaptured)
                { referenceClipboardBackup = Gui.GetClipboardTextS(); referenceClipboardCaptured = true; }
                if (ClickInspectorControl(ReferenceControl("TargetEntity") + "/reference/text")) { stage = 11020; wait = 2; }
                break;
            case 11020: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 11021: io.AddKeyEvent(ImGuiKey.A, false); io.AddKeyEvent(ImGuiKey.C, true); Next(); break;
            case 11022: io.AddKeyEvent(ImGuiKey.C, false); io.AddKeyEvent(ImGuiKey.ModCtrl, false); Next(); break;
            case 11023:
                Assert(Gui.GetClipboardTextS() == "Camera", "Read-only reference text must support selecting/copying its actual displayed value");
                RestoreReferenceClipboard();
                checks.Add("Read-only inspector reference text supports select/copy without authored mutation");
                stage = 11024; break;
            case 11024:
                if (game.Controls.Centers.ContainsKey(collection + "/add")) { stage = 11006; break; }
                if (ClickInspectorControl(collection + "/expand")) { stage = 11006; wait = 2; } break;
            case 11006:
                var items = ReferenceItems();
                Assert(items.Length >= 2, "Reference fixture must have at least two existing serialized collection entries");
                if (collectionOriginalItems.Length == 0)
                {
                    collectionOriginalTargets = items.Select(item => ((Entity)item.Value!).Id).ToArray();
                    collectionOriginalItems = items.Select(item => item.Id.ToString()).ToArray();
                    RecordAction("collection-baseline", string.Join(',', collectionOriginalTargets));
                }
                collectionSecondId = collectionOriginalItems[1];
                if (ClickInspectorControl(collection + "/up/" + collectionSecondId)) Next(); break;
            case 11007:
                var swapped = collectionOriginalTargets.ToArray(); (swapped[0], swapped[1]) = (swapped[1], swapped[0]);
                AssertCollectionTargets(swapped);
                Assert(ReferenceItems()[0].Id.ToString() == collectionSecondId, "Reorder must retain the moved native item ID");
                if (Click("toolbar/undo")) Next(); break;
            case 11008:
                AssertCollectionTargets(collectionOriginalTargets);
                if (ClickInspectorControl(collection + "/remove/" + collectionSecondId)) Next(); break;
            case 11009:
                AssertCollectionTargets(collectionOriginalTargets.Where((_, index) => index != 1));
                if (Click("toolbar/undo")) Next(); break;
            case 11010:
                AssertCollectionTargets(collectionOriginalTargets);
                Assert(ReferenceItems().Select(item => item.Id.ToString()).SequenceEqual(collectionOriginalItems),
                    "Undo remove must restore original collection item IDs and order");
                if (ClickInspectorControl(collection + "/add")) Next(); break;
            case 11011:
                if (game.Controls.Centers.ContainsKey(collection + "/add/" + referenceCamera))
                { if (Click(collection + "/add/" + referenceCamera)) { stage = 11014; wait = 2; } break; }
                var added = ReferenceItems().FirstOrDefault(item => !collectionOriginalItems.Contains(item.Id.ToString()));
                if (added == null) break;
                collectionAddedId = added.Id.ToString();
                if (ClickInspectorControl(collection + "/" + collectionAddedId + "/reference")) Next(); break;
            case 11012:
                if (Click(collection + "/" + collectionAddedId + "/reference/" + referenceCamera)) { stage = 11014; wait = 2; }
                break;
            case 11014:
                AssertCollectionTargets(collectionOriginalTargets.Append(referenceCamera));
                checks.Add("Entity reference picker and collection reorder/remove/add use real targets and stable item IDs");
                io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.S, true); Next(); break;
            case 11015: io.AddKeyEvent(ImGuiKey.S, false); io.AddKeyEvent(ImGuiKey.ModCtrl, false); Next(); break;
            case 11016:
                if (game.Document!.IsDirty) break;
                var disk = AssetFileSerializer.Load<SceneAsset>(game.Document.Path).Asset;
                var script = disk.Hierarchy.Parts[referenceSphere].Entity.Components.Single(component => component.Id == referenceScriptId);
                var type = script.GetType();
                Assert(type.GetProperty("TargetEntity")!.GetValue(script) is Entity saved && saved.Id == referenceCamera,
                    "Independent asset reload must preserve the chosen entity reference");
                var savedTargets = ((System.Collections.IEnumerable)type.GetProperty("TargetEntities")!.GetValue(script)!).Cast<Entity>().Select(entity => entity.Id);
                Assert(savedTargets.SequenceEqual(collectionOriginalTargets.Append(referenceCamera)),
                    "Independent asset reload must preserve the final collection targets/order");
                CompleteIndependentSubset(); break;
        }
    }

    private void RestoreReferenceClipboard()
    {
        if (!referenceClipboardCaptured) return;
        Gui.SetClipboardText(referenceClipboardBackup ?? "");
        referenceClipboardCaptured = false;
    }
}
