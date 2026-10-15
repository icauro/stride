namespace Stride.Editor.ImGui;

internal sealed partial class UiWorkflowTest
{
    private Guid deletedCube;
    private int modelsBeforeDelete;
    private void UpdateHierarchyDeletionWorkflow()
    {
        switch (stage)
        {
            case 140: if (Click("entity/id/" + createdRoot)) Next(); break;
            case 141: if (Click("hierarchy/delete")) Next(); break;
            case 142:
                if (game.Controls.Centers.ContainsKey("hierarchy/delete/cancel"))
                { game.CaptureDeleteCheckpoint(); stage = 143; wait = 3; } break;
            case 143: if (Click("hierarchy/delete/cancel")) Next(); break;
            case 144:
                Assert(!game.Document!.IsDirty && game.Document.Asset.Hierarchy.Parts.Count == originalPartCount + 2
                    && game.HasRuntimeEntity(createdChild, createdRoot), "Cancel deletion must preserve clean document and actual runtime subtree");
                if (Click("hierarchy/delete")) Next(); break;
            case 145: if (Click("hierarchy/delete/confirm")) Next(); break;
            case 146:
                Assert(game.Document!.IsDirty && game.Document.Selected == null && game.Document.Asset.Hierarchy.Parts.Count == originalPartCount
                    && !game.HasRuntimeEntity(createdRoot, null) && !game.HasRuntimeEntity(createdChild, createdRoot), "Confirmed deletion must remove both authored and engine entities");
                if (Click("toolbar/undo")) Next(); break;
            case 147:
                Assert(!game.Document!.IsDirty && game.Document.Selected?.Id == createdRoot && game.HasRuntimeEntity(createdRoot, null)
                    && game.HasRuntimeEntity(createdChild, createdRoot), "Undo deletion must restore subtree, selection and saved revision");
                if (Click("toolbar/redo")) Next(); break;
            case 148: if (Click("toolbar/save")) Next(); break;
            case 149: if (Click("toolbar/reload")) Next(); break;
            case 150:
                Assert(!game.Document!.IsDirty && game.Document.Asset.Hierarchy.Parts.Count == originalPartCount && !game.HasRuntimeEntity(createdRoot, null),
                    "Saved subtree deletion must remain absent after reload");
                if (Click("entity/Imported cube")) Next(); break;
            case 151:
                deletedCube = game.Document!.Selected!.Id; modelsBeforeDelete = game.EngineModelCount;
                Assert(game.EntityReferencesMatch, "Fixture references must initially map to real engine objects");
                Hexa.NET.ImGui.ImGui.GetIO().AddKeyEvent(Hexa.NET.ImGui.ImGuiKey.Delete, true); Next(); break;
            case 152:
                Hexa.NET.ImGui.ImGui.GetIO().AddKeyEvent(Hexa.NET.ImGui.ImGuiKey.Delete, false);
                if (Click("hierarchy/delete/confirm")) Next(); break;
            case 153:
                Assert(game.Document!.IsDirty && !game.HasRuntimeEntity(deletedCube, null) && game.EngineModelCount == modelsBeforeDelete - 1
                    && game.EntityReferencesMatch && game.SelectedRuntimeModels == 0, "Deleting compiled model must remove engine membership/selection and synchronize reference cleanup");
                game.CaptureDeletedModelCheckpoint(); stage = 159; wait = 3; break;
            case 159: if (Click("toolbar/undo")) { stage = 156; wait = 2; } break;
            case 156:
                Assert(!game.Document!.IsDirty && game.HasRuntimeEntity(deletedCube, null) && game.EngineModelCount == modelsBeforeDelete
                    && game.EntityReferencesMatch && game.SelectionDrawCalls > 0, "Undo model deletion must restore actual rendering, reference bindings and selected mesh feedback");
                game.CaptureDeleteUndoCheckpoint(); stage = 157; wait = 3; break;
            case 157: if (Click("toolbar/redo")) { stage = 158; wait = 2; } break;
            case 158:
                Assert(game.EngineModelCount == modelsBeforeDelete - 1 && game.EntityReferencesMatch, "Redo model deletion must clear engine membership and references again");
                if (Click("toolbar/save")) { stage = 154; wait = 2; } break;
            case 154: if (Click("toolbar/reload")) Next(); break;
            case 155:
                Assert(!game.Document!.IsDirty && !game.HasRuntimeEntity(deletedCube, null) && game.EngineModelCount == modelsBeforeDelete - 1
                    && game.EntityReferencesMatch, "Saved model deletion must suppress stale compiled content and retain cleared engine reference bindings");
                checks.Add("Delete confirmation/Cancel and hierarchy Delete key, nested subtree undo/redo/save/reload, compiled model removal/undo rendering and entity/component/list reference cleanup/restoration in authored and actual engine objects");
                stage = 170; wait = 2; break;
        }
    }
}
