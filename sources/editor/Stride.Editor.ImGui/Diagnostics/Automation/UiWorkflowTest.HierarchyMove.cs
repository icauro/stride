using Stride.Core.Mathematics;

namespace Stride.Editor.ImGui;

internal sealed partial class UiWorkflowTest
{
    private Guid movedSphere, moveParent;
    private TransformState moveOriginalLocal;
    private Matrix moveOriginalWorld;
    private void UpdateHierarchyMoveWorkflow()
    {
        bool SameWorld()
        {
            game.Document!.Selected!.Transform.UpdateWorldMatrix();
            return moveOriginalWorld.ToArray().Zip(game.Document.Selected.Transform.WorldMatrix.ToArray()).All(pair => Math.Abs(pair.First - pair.Second) < 0.0001f);
        }
        switch (stage)
        {
            case 170: if (Click("entity/Sphere")) Next(); break;
            case 171:
                movedSphere = game.Document!.Selected!.Id;
                moveParent = game.Document.Asset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Camera").Entity.Id;
                moveOriginalLocal = TransformState.Read(game.Document.Selected); game.Document.Selected.Transform.UpdateWorldMatrix();
                moveOriginalWorld = game.Document.Selected.Transform.WorldMatrix;
                if (Click("hierarchy/move")) Next(); break;
            case 172:
                if (game.Controls.Centers.ContainsKey("hierarchy/move/keep-world")) { game.CaptureMoveCheckpoint(); stage = 173; wait = 3; } break;
            case 173: if (Click("hierarchy/move/keep-world")) Next(); break;
            case 174: if (Click("hierarchy/move/" + moveParent)) Next(); break;
            case 175:
                Assert(game.Document!.IsDirty && game.Document.Selected?.Id == movedSphere && game.HasRuntimeEntity(movedSphere, moveParent)
                    && game.SelectedWorldMatches && SameWorld() && game.EntityReferencesMatch && game.SelectionDrawCalls > 0,
                    "Move menu must preserve world pose, references, selection and actual compiled model rendering under a rotated/translated parent");
                game.CaptureMovedCheckpoint(); stage = 176; wait = 3; break;
            case 176: if (Click("toolbar/undo")) Next(); break;
            case 177:
                Assert(!game.Document!.IsDirty && game.HasRuntimeEntity(movedSphere, null) && TransformState.Read(game.Document.Selected!) == moveOriginalLocal
                    && game.SelectedWorldMatches && game.EntityReferencesMatch, "Undo move must restore root/local TRS, engine hierarchy and saved revision");
                if (Click("toolbar/redo")) Next(); break;
            case 178: if (Click("toolbar/save")) Next(); break;
            case 179: if (Click("toolbar/reload")) Next(); break;
            case 180:
                Assert(!game.Document!.IsDirty && game.HasRuntimeEntity(movedSphere, moveParent) && game.EntityReferencesMatch,
                    "Saved parent change must reconcile stale compiled root placement on reload");
                if (Click("entity/id/" + movedSphere)) Next(); break;
            case 181: if (Click("hierarchy/move")) Next(); break;
            case 182: if (Click("hierarchy/move/keep-world")) Next(); break;
            case 183: if (Click("hierarchy/move/root")) Next(); break;
            case 184:
                Assert(game.HasRuntimeEntity(movedSphere, null) && game.SelectedWorldMatches && SameWorld(), "Move to scene root must preserve actual world pose");
                if (Click("toolbar/save")) Next(); break;
            case 185: if (Click("toolbar/reload")) Next(); break;
            case 186:
                Assert(!game.Document!.IsDirty && game.HasRuntimeEntity(movedSphere, null) && game.EntityReferencesMatch, "Restored root placement must save/reload with references intact");
                checks.Add("Move popup and keep-world choice, compiled mesh parent/root synchronization, unchanged world pose/references, selection/wireframe, undo/redo and save/reload");
                Passed = Finished = true; Result = "SCENE PICKING UI PASS"; break;
        }
    }
}
