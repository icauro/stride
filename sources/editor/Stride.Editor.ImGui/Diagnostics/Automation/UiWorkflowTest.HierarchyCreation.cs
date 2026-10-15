namespace Stride.Editor.ImGui;

internal sealed partial class UiWorkflowTest
{
    private Guid createdRoot, createdChild, createdTransform;
    private int originalPartCount;
    private int hierarchyIdlePasses;
    private object? hierarchyCreationEvidence;
    private void UpdateHierarchyCreationWorkflow()
    {
        switch (stage)
        {
            case 120:
                originalPartCount = game.Document!.Asset.Hierarchy.Parts.Count;
                if (Click("hierarchy/create")) Next(); break;
            case 121:
                if (game.Controls.Centers.ContainsKey("hierarchy/create/root"))
                { game.CaptureHierarchyMenuCheckpoint(); stage = 132; wait = 3; }
                break;
            case 132: if (Click("hierarchy/create/root")) { stage = 122; wait = 2; } break;
            case 122:
                var root = game.Document!.Selected!; createdRoot = root.Id; createdTransform = root.Transform.Id;
                Assert(root.Name == "Entity" && game.Document.IsDirty && game.Document.Asset.Hierarchy.RootParts.Last() == root
                    && game.HasRuntimeEntity(root.Id, null), "Root menu must create/select a real authored and engine entity");
                if (Click("toolbar/undo")) Next(); break;
            case 123:
                Assert(!game.Document!.IsDirty && game.Document.Asset.Hierarchy.Parts.Count == originalPartCount && !game.HasRuntimeEntity(createdRoot, null),
                    "Undo creation must remove asset/runtime entity and return to the clean save point");
                if (Click("toolbar/redo")) Next(); break;
            case 124:
                Assert(game.Document!.Selected?.Id == createdRoot && game.HasRuntimeEntity(createdRoot, null), "Redo must restore original identity and engine membership");
                if (Click("hierarchy/create")) Next(); break;
            case 125: if (Click("hierarchy/create/child")) Next(); break;
            case 126:
                var child = game.Document!.Selected!; createdChild = child.Id;
                Assert(child.Transform.Parent?.Entity.Id == createdRoot && game.HasRuntimeEntity(child.Id, createdRoot),
                    "Child menu must use the selected authored/runtime parent");
                hierarchyIdlePasses = game.HierarchySynchronization.Passes;
                game.CaptureHierarchyCheckpoint(); stage = 127; wait = 30; break;
            case 127:
                Assert(game.HierarchySynchronization.Passes == hierarchyIdlePasses, "Idle hierarchy frames must not repeat structural synchronization");
                hierarchyCreationEvidence = new { idleFrames = 30, idleStructuralPasses = 0, changedStructurePasses = hierarchyIdlePasses,
                    structuralCpuMilliseconds = game.HierarchySynchronization.Milliseconds,
                    boundary = "CPU structural synchronization only for this small fixture; excludes compilation/rendering/save and is not a matched WPF benchmark." };
                if (Click("toolbar/undo")) Next(); break;
            case 128:
                Assert(game.Document!.Selected?.Id == createdRoot && !game.HasRuntimeEntity(createdChild, createdRoot), "Undo child must restore selection and remove its engine instance");
                if (Click("toolbar/redo")) Next(); break;
            case 129: if (Click("toolbar/save")) Next(); break;
            case 130:
                Assert(!game.Document!.IsDirty && game.Document.Selected?.Id == createdChild, "Saving creations must establish a clean revision and retain selection");
                if (Click("toolbar/reload")) Next(); break;
            case 131:
                Assert(game.Document!.Asset.Hierarchy.Parts.Count == originalPartCount + 2 && !game.Document.IsDirty
                    && game.Document.Asset.Hierarchy.Parts[createdRoot].Entity.Transform.Id == createdTransform
                    && game.Document.Asset.Hierarchy.Parts[createdChild].Entity.Transform.Parent?.Entity.Id == createdRoot
                    && game.HasRuntimeEntity(createdRoot, null) && game.HasRuntimeEntity(createdChild, createdRoot),
                    "Save/reload must retain both authored IDs and real engine hierarchy");
                checks.Add("Empty root/child creation through menus; actual engine membership, stable IDs, selection, undo/redo and scene save/reload");
                stage = 140; wait = 2; break;
        }
    }
}
