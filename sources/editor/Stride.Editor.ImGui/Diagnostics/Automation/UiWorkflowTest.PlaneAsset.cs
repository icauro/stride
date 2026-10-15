using Hexa.NET.ImGui;
using Stride.Assets.Models;
using Stride.Core.Assets;
using Stride.Core.Mathematics;
using Stride.Graphics.GeometricPrimitives;
using Stride.Rendering.ProceduralModels;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class UiWorkflowTest
{
    private SceneDocument? planeScene;
    private Matrix planeSceneCamera;
    private ModelAssetPreview? oldPlanePreview;
    private object? planeEditingEvidence;
    private int planeVectorRound, planeReturnStage;
    private string PlaneVectorField => planeVectorRound == 0 ? "Size" : "Tessellation";
    private PlaneProceduralModel EditedPlane => (PlaneProceduralModel)game.AssetDocument!.Asset.Type;
    private void StartPlaneVectorInput(int round, int returnStage)
    { planeVectorRound = round; planeReturnStage = returnStage; stage = 100; wait = 2; }
    private void AssertPlaneIsolation()
    {
        Assert(ReferenceEquals(game.Document, planeScene) && game.SceneCameraView == planeSceneCamera
            && !game.Document!.IsDirty && !game.Document.CanUndo, "Plane asset work must retain the original clean scene document/history/camera");
    }
    private void UpdatePlaneAssetWorkflow()
    {
        if (stage >= 400) { UpdateModelTypeWorkflow(); return; }
        var io = Gui.GetIO();
        switch (stage)
        {
            case 0: if (Click("toolbar/open")) Next(); break;
            case 1: if (Click("project/open")) Next(); break;
            case 2:
                if (game.Document != null && game.EngineDrawCalls > 0)
                { planeScene = game.Document; planeSceneCamera = game.SceneCameraView; if (Click("asset/filter")) Next(); }
                break;
            case 3: foreach (var c in "Ground") io.AddInputCharacter(c); Next(); break;
            case 4: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 5: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 6: if (Click("asset/Ground")) Next(); break;
            case 7:
                if (game.ModelPreview is { RenderCount: > 0 } preview)
                { oldPlanePreview = preview; if (Click("model-preview/edit")) Next(); }
                break;
            case 8:
                if (game.AssetDocument != null) StartPlaneVectorInput(0, 9); break;
            case 100: if (Click("asset-editor/field/" + PlaneVectorField + "/axis/X")) { stage = 101; wait = 2; } break;
            case 101: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); stage = 102; wait = 2; break;
            case 102:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var c in planeVectorRound == 0 ? "12.5" : "3") io.AddInputCharacter(c); stage = 103; wait = 2; break;
            case 103: if (Click("asset-editor/field/" + PlaneVectorField + "/axis/Y")) { stage = 104; wait = 2; } break;
            case 104: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); stage = 105; wait = 2; break;
            case 105:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var c in planeVectorRound == 0 ? "7.25" : "4") io.AddInputCharacter(c); stage = 106; wait = 2; break;
            case 106: if (Click("asset-editor/field/Normal")) { stage = 107; wait = 2; } break;
            case 107: io.AddKeyEvent(ImGuiKey.Escape, true); stage = 108; wait = 2; break;
            case 108: io.AddKeyEvent(ImGuiKey.Escape, false); stage = planeReturnStage; wait = 2; break;
            case 9:
                Assert(EditedPlane.Size == new Vector2(12.5f, 7.25f), "Float plane vector widgets must preserve both edited axes");
                if (Click("asset-editor/undo")) Next(); break;
            case 10:
                Assert(EditedPlane.Size == new Vector2(10) && !game.AssetDocument!.IsDirty && !game.AssetDocument.CanUndo,
                    "Moving directly between vector axes must undo as one asset operation");
                if (Click("asset-editor/redo")) Next(); break;
            case 11:
                Assert(EditedPlane.Size == new Vector2(12.5f, 7.25f), "Plane size redo must retain both values");
                StartPlaneVectorInput(1, 12); break;
            case 12:
                Assert(EditedPlane.Tessellation == new Int2(3, 4), "Integer plane vector widgets must retain exact values");
                if (Click("asset-editor/undo")) Next(); break;
            case 13:
                Assert(EditedPlane.Tessellation == new Int2(1) && EditedPlane.Size == new Vector2(12.5f, 7.25f), "Tessellation undo must preserve size");
                if (Click("asset-editor/redo")) Next(); break;
            case 14: if (Click("asset-editor/field/Normal")) Next(); break;
            case 15: if (Click("asset-editor/field/Normal/option/UpX")) Next(); break;
            case 16:
                Assert(EditedPlane.Normal == NormalDirection.UpX, "Asset enum must edit the authored normal direction");
                if (Click("asset-editor/undo")) Next(); break;
            case 17:
                Assert(EditedPlane.Normal == NormalDirection.UpY, "Enum must undo in one discrete transaction");
                if (Click("asset-editor/redo")) Next(); break;
            case 18: if (Click("asset-editor/field/GenerateBackFace")) Next(); break;
            case 19:
                Assert(EditedPlane.GenerateBackFace, "Asset checkbox must update back-face generation");
                if (Click("asset-editor/undo")) Next(); break;
            case 20:
                Assert(!EditedPlane.GenerateBackFace, "Asset checkbox must have independent undo");
                if (Click("asset-editor/redo")) Next(); break;
            case 21: if (Click("asset-editor/save")) Next(); break;
            case 22:
                var saved = (PlaneProceduralModel)AssetFileSerializer.Load<ProceduralModelAsset>(game.AssetDocument!.Path).Asset.Type;
                Assert(saved.Size == new Vector2(12.5f, 7.25f) && saved.Tessellation == new Int2(3, 4) && saved.Normal == NormalDirection.UpX && saved.GenerateBackFace,
                    "Actual asset YAML must retain vector/enum/checkbox values");
                if (Click("asset-editor/update")) Next(); break;
            case 23:
                if (!game.IsAssetPreviewUpdating && game.ModelPreview is { RenderAgeSeconds: >= 5 } updated && !ReferenceEquals(updated, oldPlanePreview))
                {
                    AssertPlaneIsolation();
                    var expected = new Vector3(0, 7.25f, 12.5f);
                    Assert(updated.ModelSize == expected && game.RuntimeAssetSize(updated.Name) == expected && updated.ModelFitsCamera
                        && updated.VertexBytes > oldPlanePreview!.VertexBytes && oldPlanePreview.IsDisposed, "Compiled UpX plane size/tessellation/back faces must reach real preview and scene GPU resources");
                    planeEditingEvidence = new { assetUrl = updated.Name, size = "12.5,7.25", tessellation = "3,4", normal = "UpX", backFace = true,
                        modelSize = updated.ModelSize.ToString(), vertexBytes = updated.VertexBytes, drawCalls = updated.DrawCalls,
                        sceneDocumentRetained = ReferenceEquals(game.Document, planeScene), cameraPreserved = game.SceneCameraView == planeSceneCamera };
                    if (Click("model-preview/backdrop")) Next();
                }
                break;
            case 24: if (Click("model-preview/backdrop/Light")) Next(); break;
            case 25:
                if (game.ModelPreview is { RenderAgeSeconds: >= 5 }) { io.AddMousePosEvent(700, 120); Next(); } break;
            case 26: game.CapturePlaneUpdateCheckpoint(); if (Click("model-preview/edit")) Next(); break;
            case 27: io.AddMousePosEvent(700, 120); Next(); break;
            case 28: game.CapturePlaneEditorCheckpoint(); if (Click("toolbar/reload")) Next(); break;
            case 29:
                Assert(EditedPlane.Normal == NormalDirection.UpX && EditedPlane.Size == new Vector2(12.5f, 7.25f) && !game.AssetDocument!.CanUndo,
                    "Reload must reopen the saved asset with clean history");
                if (Click("asset-editor/close")) Next(); break;
            case 30: if (Click("asset/Ground")) Next(); break;
            case 31:
                if (game.ModelPreview is { RenderCount: > 0 } reopened)
                {
                    Assert(reopened.ModelFitsCamera && reopened.Camera.DefaultYaw == MathUtil.PiOverTwo, "Reopened plane preview must use saved UpX orientation rather than stale package metadata");
                    if (Click("model-preview/edit")) Next();
                }
                break;
            case 32:
                if (game.Controls.Centers.TryGetValue("asset-editor/field/GenerateBackFace", out var scrollPoint))
                { io.AddMousePosEvent(scrollPoint.X, scrollPoint.Y); io.AddMouseWheelEvent(0, -20); stage = 300; wait = 3; }
                break;
            case 300: if (Click("asset-editor/material/choose")) { stage = 301; wait = 2; } break;
            case 301: if (Click("material-picker/asset//EditorParity/Sphere Material")) { stage = 302; wait = 2; } break;
            case 302: game.CaptureMaterialPickerCheckpoint(); if (Click("material-picker/cancel")) { stage = 303; wait = 2; } break;
            case 303:
                Assert(MaterialReference().Url == "Ground Material" && !game.AssetDocument!.IsDirty, "Picker Cancel must leave authored reference/history unchanged");
                if (Click("asset-editor/material/choose")) { stage = 304; wait = 2; } break;
            case 304: if (Click("material-picker/none")) { stage = 305; wait = 2; } break;
            case 305: if (Click("material-picker/use")) { stage = 306; wait = 2; } break;
            case 306:
                Assert(EditedPlane.MaterialInstance.Material == null && game.AssetDocument!.IsDirty, "None must clear the real material dependency");
                if (Click("asset-editor/undo")) { stage = 307; wait = 2; } break;
            case 307:
                Assert(MaterialReference().Url == "Ground Material" && !game.AssetDocument!.IsDirty, "Reference undo must return to saved dependency");
                if (Click("asset-editor/material/choose")) { stage = 308; wait = 2; } break;
            case 308: if (Click("material-picker/asset//EditorParity/Sphere Material")) { stage = 309; wait = 2; } break;
            case 309: if (Click("material-picker/use")) { stage = 310; wait = 2; } break;
            case 310:
                Assert(MaterialReference().Url.EndsWith("Sphere Material"), "Use must assign selected project material");
                if (Click("asset-editor/undo")) { stage = 311; wait = 2; } break;
            case 311:
                Assert(MaterialReference().Url == "Ground Material", "Material selection must undo atomically");
                if (Click("asset-editor/redo")) { stage = 312; wait = 2; } break;
            case 312: if (Click("asset-editor/field/IsShadowCaster")) { stage = 313; wait = 2; } break;
            case 313:
                Assert(!EditedPlane.MaterialInstance.IsShadowCaster, "Cast Shadows must edit the real material instance");
                if (Click("asset-editor/undo")) { stage = 314; wait = 2; } break;
            case 314:
                Assert(EditedPlane.MaterialInstance.IsShadowCaster && MaterialReference().Url.EndsWith("Sphere Material"), "Shadow undo must preserve the selected material");
                if (Click("asset-editor/redo")) { stage = 315; wait = 2; } break;
            case 315: game.CaptureMaterialEditorCheckpoint(); if (Click("asset-editor/update")) { stage = 316; wait = 2; } break;
            case 316:
                if (!game.IsAssetPreviewUpdating && game.ModelPreview is { RenderAgeSeconds: >= 5 } materialPreview)
                {
                    var runtimeMaterial = game.RuntimeAssetMaterial(materialPreview.Name);
                    Assert(materialPreview.MaterialUrl?.EndsWith("Sphere Material") == true && !materialPreview.CastsShadows
                        && runtimeMaterial.Url?.EndsWith("Sphere Material") == true && !runtimeMaterial.CastsShadows,
                        "Compiled chosen material and shadow flag must reach actual preview and scene Model resources");
                    var savedMaterial = ((PlaneProceduralModel)AssetFileSerializer.Load<ProceduralModelAsset>(game.AssetDocument!.Path).Asset.Type).MaterialInstance;
                    Assert(Stride.Core.Serialization.AttachedReferenceManager.GetAttachedReference(savedMaterial.Material)!.Id == MaterialReference().Id
                        && !savedMaterial.IsShadowCaster, "Saved material must retain selected asset ID and instance flag");
                    AssertPlaneIsolation();
                    if (Click("model-preview/edit")) { stage = 317; wait = 2; }
                }
                break;
            case 317: if (Click("toolbar/reload")) { stage = 318; wait = 2; } break;
            case 318:
                Assert(MaterialReference().Url.EndsWith("Sphere Material") && !EditedPlane.MaterialInstance.IsShadowCaster
                    && !game.AssetDocument!.CanUndo, "Reload must retain chosen material/shadow YAML with clean history");
                io.AddMousePosEvent(650, 500); io.AddMouseWheelEvent(0, -20); stage = 319; wait = 3; break;
            case 319: if (Click("asset-editor/material/choose")) { stage = 320; wait = 2; } break;
            case 320: if (Click("material-picker/asset//EditorParity/Ground Material")) { stage = 321; wait = 2; } break;
            case 321: if (Click("material-picker/use")) { stage = 322; wait = 2; } break;
            case 322: if (Click("asset-editor/field/IsShadowCaster")) { stage = 323; wait = 2; } break;
            case 323:
                Assert(MaterialReference().Url.EndsWith("Ground Material") && EditedPlane.MaterialInstance.IsShadowCaster, "Restore original material and shadow setting through UI");
                io.AddMousePosEvent(650, 500); io.AddMouseWheelEvent(0, 20); stage = 324; wait = 3; break;
            case 324:
                // Restore through UI: inverse independent operations are verified in a fresh editor below.
                restoreField = 0; stage = 200; wait = 2; break;
            case 200: if (Click("asset-editor/field/" + RestorePlaneField)) { stage = 201; wait = 2; } break;
            case 201: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); stage = 202; wait = 2; break;
            case 202:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var c in restoreField < 2 ? "10" : "1") io.AddInputCharacter(c); stage = 203; wait = 2; break;
            case 203: io.AddKeyEvent(ImGuiKey.Enter, true); stage = 204; wait = 2; break;
            case 204: io.AddKeyEvent(ImGuiKey.Enter, false); if (++restoreField < 4) stage = 200; else stage = 205; wait = 2; break;
            case 205: if (Click("asset-editor/field/Normal")) { stage = 206; wait = 2; } break;
            case 206: if (Click("asset-editor/field/Normal/option/UpY")) { stage = 207; wait = 2; } break;
            case 207: if (Click("asset-editor/field/GenerateBackFace")) { stage = 208; wait = 2; } break;
            case 208:
                Assert(EditedPlane.Size == new Vector2(10) && EditedPlane.Tessellation == new Int2(1) && EditedPlane.Normal == NormalDirection.UpY && !EditedPlane.GenerateBackFace,
                    "UI restoration must retain the original plane parameters");
                if (Click("asset-editor/update")) { stage = 209; wait = 2; } break;
            case 209:
                if (!game.IsAssetPreviewUpdating && game.ModelPreview is { RenderAgeSeconds: >= 5 } original)
                {
                    Assert(original.ModelSize == new Vector3(10, 0, 10) && game.RuntimeAssetSize(original.Name) == original.ModelSize
                        && original.VertexBytes == 336 && game.RetiredModelOwners == 0 && game.RefreshedModelOwners == 1, "Restored plane must rebuild original geometry and retire superseded owners");
                    AssertPlaneIsolation(); checks.Add("Plane float/integer axis grouping, enum/checkbox history, YAML save/reload, real UpX GPU preview/scene refresh, saved orientation reopen and UI restoration");
                    Assert(original.MaterialUrl?.EndsWith("Ground Material") == true && original.CastsShadows
                        && game.RuntimeAssetMaterial(original.Name).CastsShadows, "Restoration must compile original material and shadow flag");
                    checks.Add("Material picker Cancel/None/Use, atomic reference and shadow undo/redo, dependency ID/YAML reload, actual compiled preview/scene material and restoration");
                    stage = 400; wait = 2;
                }
                break;
        }
    }
    private int restoreField;
    private Stride.Core.Serialization.AttachedReference MaterialReference() => Stride.Core.Serialization.AttachedReferenceManager.GetAttachedReference(EditedPlane.MaterialInstance.Material)!;
    private string RestorePlaneField => (restoreField < 2 ? "Size" : "Tessellation") + "/axis/" + (restoreField % 2 == 0 ? "X" : "Y");
}
