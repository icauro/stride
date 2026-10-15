using Hexa.NET.ImGui;
using Stride.Assets.Entities;
using Stride.Core.Assets;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class UiWorkflowTest
{
    private Guid isolatedRoot, isolatedChild, isolatedTransform, isolatedLight, isolatedSphere;
    private int isolatedInitialPartCount;
    private float isolatedInitialIntensity;

    private void UpdateHierarchyInspectorScenario()
    {
        var io = Gui.GetIO();
        switch (stage)
        {
            case 10500:
                isolatedInitialPartCount = game.Document!.Asset.Hierarchy.Parts.Count;
                Assert(!game.Document.IsDirty, "Hierarchy fixture must start clean");
                if (Click("hierarchy/create")) Next(); break;
            case 10501: if (Click("hierarchy/create/root")) Next(); break;
            case 10502:
                var root = game.Document!.Selected;
                if (root == null || !game.HasRuntimeEntity(root.Id, null)) break;
                isolatedRoot = root.Id; isolatedTransform = root.Transform.Id;
                Assert(game.Document.Asset.Hierarchy.Parts.Count == isolatedInitialPartCount + 1
                    && game.Document.Asset.Hierarchy.RootParts.Last().Id == root.Id && game.Document.IsDirty,
                    "Create root must publish and select the actual authored/runtime root");
                if (Click("toolbar/undo")) Next(); break;
            case 10503:
                Assert(game.Document!.Asset.Hierarchy.Parts.Count == isolatedInitialPartCount
                    && !game.HasRuntimeEntity(isolatedRoot, null) && !game.Document.IsDirty,
                    "Undo root creation must remove the same runtime identity and restore save point");
                if (Click("toolbar/redo")) Next(); break;
            case 10504:
                if (!game.HasRuntimeEntity(isolatedRoot, null)) break;
                Assert(game.Document!.Selected?.Id == isolatedRoot, "Redo must restore original root selection and identity");
                if (Click("hierarchy/create")) Next(); break;
            case 10505: if (Click("hierarchy/create/child")) Next(); break;
            case 10506:
                var child = game.Document!.Selected;
                if (child == null || child.Id == isolatedRoot || !game.HasRuntimeEntity(child.Id, isolatedRoot)) break;
                isolatedChild = child.Id;
                Assert(child.Transform.Parent?.Entity.Id == isolatedRoot && game.Document.Asset.Hierarchy.Parts.Count == isolatedInitialPartCount + 2,
                    "Create child must use the actual selected root as authored/runtime parent");
                if (Click("toolbar/undo")) Next(); break;
            case 10507:
                Assert(!game.Document!.Asset.Hierarchy.Parts.ContainsKey(isolatedChild)
                    && !game.HasRuntimeEntity(isolatedChild, isolatedRoot) && game.Document.Selected?.Id == isolatedRoot,
                    "Undo child creation must remove it and restore root selection");
                if (Click("toolbar/redo")) Next(); break;
            case 10508:
                if (!game.HasRuntimeEntity(isolatedChild, isolatedRoot)) break;
                Assert(game.Document!.Selected?.Id == isolatedChild, "Redo child must preserve original child identity/selection");
                io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.S, true); Next(); break;
            case 10509: io.AddKeyEvent(ImGuiKey.S, false); io.AddKeyEvent(ImGuiKey.ModCtrl, false); Next(); break;
            case 10510:
                if (game.Document!.IsDirty) break;
                var hierarchyDisk = AssetFileSerializer.Load<SceneAsset>(game.Document.Path).Asset;
                Assert(hierarchyDisk.Hierarchy.Parts.Count == isolatedInitialPartCount + 2
                    && hierarchyDisk.Hierarchy.Parts[isolatedRoot].Entity.Transform.Id == isolatedTransform
                    && hierarchyDisk.Hierarchy.Parts[isolatedChild].Entity.Transform.Parent?.Entity.Id == isolatedRoot,
                    "Independently loaded scene must retain created hierarchy/component IDs and parent links");
                checks.Add("Root/child menu creation, real runtime identities, undo/redo/selection and persisted hierarchy");
                Next(); break;
            case 10511: if (Click("entity/id/" + isolatedChild)) Next(); break;
            case 10512: io.AddKeyEvent(ImGuiKey.F2, true); Next(); break;
            case 10513: io.AddKeyEvent(ImGuiKey.F2, false); Next(); break;
            case 10514:
                if (game.Controls.Centers.ContainsKey("rename/name"))
                { io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); }
                break;
            case 10515:
                io.AddKeyEvent(ImGuiKey.A, false); io.AddKeyEvent(ImGuiKey.ModCtrl, false);
                foreach (var character in "Parity child") io.AddInputCharacter(character);
                Next(); break;
            case 10516: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 10517: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 10518:
                Assert(game.Document!.Selected?.Id == isolatedChild && game.Document.Selected.Name == "Parity child"
                    && game.SelectedEngineNameMatches, "F2 rename must preserve child identity and update runtime name");
                if (Click("entity/id/" + isolatedChild, 1)) Next(); break;
            case 10519: if (Click("hierarchy/move")) Next(); break;
            case 10520: if (Click("hierarchy/move/keep-world")) Next(); break;
            case 10521: if (Click("hierarchy/move/root")) Next(); break;
            case 10522:
                if (!game.HasRuntimeEntity(isolatedChild, null)) break;
                Assert(game.Document!.Asset.Hierarchy.Parts[isolatedChild].Entity.Transform.Parent == null
                    && game.Document.Selected?.Id == isolatedChild && game.SelectedWorldMatches,
                    "Move-to-root UI must reparent the same authored/runtime child and preserve world pose");
                if (Click("toolbar/undo")) Next(); break;
            case 10523:
                if (!game.HasRuntimeEntity(isolatedChild, isolatedRoot)) break;
                Assert(game.Document!.Asset.Hierarchy.Parts[isolatedChild].Entity.Transform.Parent?.Entity.Id == isolatedRoot,
                    "Undo move must restore the actual original parent");
                if (Click("entity/id/" + isolatedRoot)) Next(); break;
            case 10524: io.AddKeyEvent(ImGuiKey.Delete, true); Next(); break;
            case 10525: io.AddKeyEvent(ImGuiKey.Delete, false); Next(); break;
            case 10526: if (Click("hierarchy/delete/cancel")) Next(); break;
            case 10527:
                Assert(game.Document!.Asset.Hierarchy.Parts.ContainsKey(isolatedRoot)
                    && game.HasRuntimeEntity(isolatedChild, isolatedRoot), "Cancel subtree deletion must retain authored/runtime descendants");
                if (Click("entity/id/" + isolatedRoot)) Next(); break;
            case 10528: io.AddKeyEvent(ImGuiKey.Delete, true); Next(); break;
            case 10529: io.AddKeyEvent(ImGuiKey.Delete, false); Next(); break;
            case 10530: if (Click("hierarchy/delete/confirm")) Next(); break;
            case 10531:
                Assert(!game.Document!.Asset.Hierarchy.Parts.ContainsKey(isolatedRoot)
                    && !game.Document.Asset.Hierarchy.Parts.ContainsKey(isolatedChild)
                    && !game.HasRuntimeEntity(isolatedRoot, null) && !game.HasRuntimeEntity(isolatedChild, isolatedRoot),
                    "Confirmed subtree deletion must remove the exact authored/runtime descendants");
                if (Click("toolbar/undo")) Next(); break;
            case 10532:
                if (!game.HasRuntimeEntity(isolatedRoot, null) || !game.HasRuntimeEntity(isolatedChild, isolatedRoot)) break;
                Assert(game.Document!.Asset.Hierarchy.Parts[isolatedChild].Entity.Name == "Parity child",
                    "Undo deletion must preserve the renamed child's stable identity and parent");
                if (Click("hierarchy/filter")) Next(); break;
            case 10533: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 10534:
                io.AddKeyEvent(ImGuiKey.A, false); io.AddKeyEvent(ImGuiKey.ModCtrl, false);
                foreach (var character in "Parity child") io.AddInputCharacter(character);
                Next(); break;
            case 10535: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 10536: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 10537: if (Click("entity/id/" + isolatedChild)) Next(); break;
            case 10538:
                Assert(game.Document!.Selected?.Id == isolatedChild, "Filtered hierarchy must select the matching stable child identity");
                if (Click("hierarchy/filter")) Next(); break;
            case 10539: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 10540: io.AddKeyEvent(ImGuiKey.A, false); io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.Backspace, true); Next(); break;
            case 10541: io.AddKeyEvent(ImGuiKey.Backspace, false); io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 10542: io.AddKeyEvent(ImGuiKey.Enter, false); io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.S, true); Next(); break;
            case 10543: io.AddKeyEvent(ImGuiKey.S, false); io.AddKeyEvent(ImGuiKey.ModCtrl, false); Next(); break;
            case 10544:
                if (game.Document!.IsDirty) break;
                var lifecycleDisk = AssetFileSerializer.Load<SceneAsset>(game.Document.Path).Asset;
                Assert(lifecycleDisk.Hierarchy.Parts[isolatedChild].Entity.Name == "Parity child"
                    && lifecycleDisk.Hierarchy.Parts[isolatedChild].Entity.Transform.Parent?.Entity.Id == isolatedRoot,
                    "Independent save/reopen must preserve renamed and restored hierarchy IDs/parents");
                checks.Add("F2 rename, move-to-root/undo, subtree deletion Cancel/confirm/undo, search selection and persistence");
                CompleteIndependentSubset(); break;

            case 10600: if (Click("entity/Sphere")) Next(); break;
            case 10601:
                if (game.Document?.Selected?.Name != "Sphere") break;
                isolatedSphere = game.Document.Selected.Id;
                Assert(game.Document.Selected.Transform.Position.X != 2.5f, "Inspector fixture must begin at a distinct vector value");
                if (Click("Position/0")) Next(); break;
            case 10602: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 10603:
                io.AddKeyEvent(ImGuiKey.A, false); io.AddKeyEvent(ImGuiKey.ModCtrl, false);
                foreach (var character in "2.5") io.AddInputCharacter(character);
                Next(); break;
            case 10604: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 10605: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 10606:
                AssertPosition(2.5f); Assert(game.Document!.IsDirty && game.Document.CanUndo, "Vector-axis widget must create authored history");
                if (Click("entity/Directional light")) Next(); break;
            case 10607:
                if (game.Document?.Selected?.Name != "Directional light") break;
                isolatedLight = game.Document.Selected.Id;
                isolatedInitialIntensity = game.Document.Selected.Get<LightComponent>()!.Intensity;
                Assert(isolatedInitialIntensity != 10f, "Inspector light fixture must begin with a distinct scalar");
                var header = "component/" + LightField.Component.Id + "/header";
                if (game.Controls.Expanded.TryGetValue(header, out var expanded) && !expanded)
                { Click(header); break; }
                if (Click(LightFieldControl)) Next(); break;
            case 10608: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 10609:
                io.AddKeyEvent(ImGuiKey.A, false); io.AddKeyEvent(ImGuiKey.ModCtrl, false);
                foreach (var character in "10") io.AddInputCharacter(character);
                Next(); break;
            case 10610: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 10611:
                io.AddKeyEvent(ImGuiKey.Enter, false); stage = 10617; wait = 2; break;
            case 10617:
                // Retained numeric gestures commit on field deactivation. Leave
                // through actual hierarchy input, as the existing scalar UI test.
                if (Click("entity/Directional light")) { stage = 10612; wait = 2; }
                break;
            case 10612:
                AssertLight(10f);
                if (Click("toolbar/undo")) Next(); break;
            case 10613:
                AssertLight(isolatedInitialIntensity);
                Assert(game.Document!.Asset.Hierarchy.Parts[isolatedSphere].Entity.Transform.Position.X == 2.5f && game.Document.IsDirty,
                    "Undo scalar must preserve the independent prior vector edit and dirty revision");
                if (Click("toolbar/redo")) Next(); break;
            case 10614:
                AssertLight(10f);
                io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.S, true); Next(); break;
            case 10615: io.AddKeyEvent(ImGuiKey.S, false); io.AddKeyEvent(ImGuiKey.ModCtrl, false); Next(); break;
            case 10616:
                if (game.Document!.IsDirty) break;
                var inspectorDisk = AssetFileSerializer.Load<SceneAsset>(game.Document.Path).Asset;
                Assert(inspectorDisk.Hierarchy.Parts[isolatedSphere].Entity.Transform.Position.X == 2.5f
                    && inspectorDisk.Hierarchy.Parts[isolatedLight].Entity.Get<LightComponent>()!.Intensity == 10f,
                    "Independently loaded asset must retain both vector and scalar widget edits by entity ID");
                checks.Add("Vector-axis/scalar widgets, actual runtime equality, scalar undo/redo and independent persistence");
                CompleteIndependentSubset(); break;
        }
    }

    private void CompleteIndependentSubset()
    {
        Passed = Finished = true;
        Result = "UI " + scenario + " subset PASS: " + string.Join("; ", checks) + ". " + ScenarioScope;
        if (ContinueInspectorGroup()) return;
    }
}
